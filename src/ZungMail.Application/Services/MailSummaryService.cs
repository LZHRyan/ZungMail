using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Microsoft.Extensions.Logging;

namespace ZungMail.Application.Services;

/// <summary>
/// 摘要编排：读正文（必要时先取正文）→ 清洗裁剪 → 按设置选引擎（不可用自动降级）→ 生成 → 单封缓存。
/// <para>
/// 这里不包含任何推理实现：本地小模型与规则抽取都只是 <see cref="IMailSummaryProvider"/>，
/// 换引擎（包括将来接云端）不需要改这个类与界面。
/// </para>
/// </summary>
public sealed class MailSummaryService : IMailSummaryService
{
    /// <summary>本地模型单封推理上限：超过就降级，避免用户干等。</summary>
    private static readonly TimeSpan LocalModelTimeout = TimeSpan.FromSeconds(25);

    /// <summary>规则抽取几乎瞬间完成，给 10 秒足够暴露异常。</summary>
    private static readonly TimeSpan RuleBasedTimeout = TimeSpan.FromSeconds(10);

    private readonly IMessageRepository _messages;
    private readonly IMailSyncService _sync;
    private readonly ISettingsStore _settings;
    private readonly IReadOnlyList<IMailSummaryProvider> _providers;
    private readonly ILogger<MailSummaryService> _logger;

    /// <summary>单封缓存：key = 邮件 Id，value = 输入指纹 + 结果（正文或引擎变了就重算）。</summary>
    private readonly ConcurrentDictionary<long, CacheEntry> _cache = new();

    /// <summary>可注入的超时（测试用短超时验证降级；生产用上面的默认值）。</summary>
    private readonly TimeSpan _localModelTimeout;
    private readonly TimeSpan _ruleBasedTimeout;

    public MailSummaryService(
        IMessageRepository messages,
        IMailSyncService sync,
        ISettingsStore settings,
        IEnumerable<IMailSummaryProvider> providers,
        ILogger<MailSummaryService> logger,
        TimeSpan? localModelTimeout = null,
        TimeSpan? ruleBasedTimeout = null)
    {
        _messages = messages;
        _sync = sync;
        _settings = settings;
        _providers = providers.ToList();
        _logger = logger;
        _localModelTimeout = localModelTimeout ?? LocalModelTimeout;
        _ruleBasedTimeout = ruleBasedTimeout ?? RuleBasedTimeout;
    }

    public string PreferredProviderId
        => MailSummarySettings.NormalizeProvider(_settings.Current.MailSummaryProvider);

    public IReadOnlyList<MailSummaryProviderStatus> DescribeProviders()
    {
        var localAiEnabled = _settings.Current.LocalAiEnabled;
        return _providers
            .Select(provider =>
            {
                var status = SafeStatus(provider);
                // 用户关掉本地 AI 时，本地模型按「不可用」上报，设置页据此显示已关闭
                if (!localAiEnabled &&
                    string.Equals(provider.Id, MailSummarySettings.LocalModelProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    return new MailSummaryProviderStatus
                    {
                        Available = false,
                        NeedsModel = status.NeedsModel,
                        UnavailableReason = "已关闭本地 AI"
                    };
                }

                return status;
            })
            .ToList();
    }

    public void ResetProviders()
    {
        foreach (var provider in _providers) provider.ResetState();
    }

    public MailSummaryResult? TryGetCached(long messageId)
        => _cache.TryGetValue(messageId, out var entry) ? entry.Result : null;

    public async Task<MailSummaryResult> SummarizeAsync(
        long messageId,
        bool force = false,
        IProgress<MailSummaryProgress>? progress = null,
        CancellationToken ct = default)
    {
        var settings = _settings.Current;

        progress?.Report(new MailSummaryProgress("正在读取邮件", 0.05));

        var detail = await _messages.GetDetailAsync(messageId, ct).ConfigureAwait(false)
                     ?? throw new MailSummaryUnavailableException("邮件已不存在，可能已被删除。");

        var input = MailSummaryInputBuilder.Build(
            detail.BodyText, detail.BodyHtml, MailSummarySettings.EffectiveInputChars(settings.MailSummaryInputChars));

        // 只有邮件头时先补一次正文，否则没有东西可总结
        if (!input.HasContent && detail.BodyState != BodyState.Downloaded)
        {
            progress?.Report(new MailSummaryProgress("正在获取正文", 0.1));
            bool downloaded;
            try
            {
                downloaded = await _sync.DownloadBodyAsync(detail.Id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "摘要前获取正文失败：邮件 {Id}", messageId);
                downloaded = false;
            }

            if (downloaded)
            {
                detail = await _messages.GetDetailAsync(messageId, ct).ConfigureAwait(false) ?? detail;
                input = MailSummaryInputBuilder.Build(
                    detail.BodyText, detail.BodyHtml,
                    MailSummarySettings.EffectiveInputChars(settings.MailSummaryInputChars));
            }
        }

        if (!input.HasContent)
        {
            throw new MailSummaryUnavailableException("这封邮件没有可总结的正文（可能还在服务器上或正文下载失败）。");
        }

        var fingerprint = Fingerprint(input.Text, settings.MailSummaryProvider, settings.LocalAiEnabled);
        if (!force && _cache.TryGetValue(messageId, out var cached) && cached.Fingerprint == fingerprint)
        {
            return cached.Result;
        }

        var request = new MailSummaryRequest
        {
            MessageId = detail.Id,
            Subject = detail.Subject,
            FromDisplay = detail.FromDisplay,
            DateUtc = detail.DateUtc,
            InputText = input.Text,
            Truncated = input.Truncated,
            ChunkChars = MailSummarySettings.ChunkChars,
            Length = MailSummarySettings.ResolveLength(settings.MailSummaryLength)
        };

        var order = MailSummarySettings.ProviderOrder(settings.MailSummaryProvider);
        var firstRequested = order.Count > 0 ? order[0] : MailSummarySettings.RuleBasedProviderId;
        string? fallbackReason = null;

        foreach (var providerId in order)
        {
            ct.ThrowIfCancellationRequested();

            var provider = _providers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                fallbackReason ??= "所选摘要引擎未安装";
                continue;
            }

            // 本地 AI 没启用就不碰本地模型：不加载、不推理，直接走规则抽取
            if (!settings.LocalAiEnabled &&
                string.Equals(provider.Id, MailSummarySettings.LocalModelProviderId, StringComparison.OrdinalIgnoreCase))
            {
                fallbackReason ??= "本地 AI 未启用，改用规则抽取";
                continue;
            }

            var status = SafeStatus(provider);
            if (!status.Available)
            {
                fallbackReason ??= status.NeedsModel
                    ? "本地模型尚未下载，已改用规则抽取"
                    : "本地模型当前不可用（" + (status.UnavailableReason ?? "未知原因") + "），已改用规则抽取";
                continue;
            }

            var elapsed = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(providerId == MailSummarySettings.LocalModelProviderId
                ? _localModelTimeout
                : _ruleBasedTimeout);

            try
            {
                var result = await provider.SummarizeAsync(request, progress, timeout.Token).ConfigureAwait(false);
                elapsed.Stop();

                var finalResult = new MailSummaryResult
                {
                    MessageId = result.MessageId,
                    ProviderId = result.ProviderId,
                    ProviderName = result.ProviderName,
                    Bullets = result.Bullets,
                    Actions = result.Actions,
                    References = result.References,
                    RawText = result.RawText,
                    Truncated = result.Truncated || input.Truncated,
                    IsFallback = fallbackReason is not null ||
                                 !string.Equals(provider.Id, firstRequested, StringComparison.OrdinalIgnoreCase),
                    FallbackReason = fallbackReason,
                    InputChars = result.InputChars > 0 ? result.InputChars : input.Text.Length,
                    ChunkCount = Math.Max(1, result.ChunkCount),
                    ElapsedMs = result.ElapsedMs > 0 ? result.ElapsedMs : elapsed.ElapsedMilliseconds
                };

                _cache[messageId] = new CacheEntry(fingerprint, finalResult);
                _logger.LogInformation(
                    "邮件摘要完成：邮件 {Id}，引擎 {Provider}，{Chars} 字 / {Chunks} 段，{Elapsed} ms，降级 {Fallback}",
                    messageId, finalResult.ProviderId, finalResult.InputChars, finalResult.ChunkCount,
                    finalResult.ElapsedMs, finalResult.FallbackReason ?? "否");

                return finalResult;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                fallbackReason ??= provider.Id == MailSummarySettings.LocalModelProviderId
                    ? $"本地模型超过 {FormatTimeout(_localModelTimeout)} 未完成，已改用规则抽取"
                    : "摘要引擎超时";
                _logger.LogInformation("摘要引擎超时：{Provider}，邮件 {Id}", provider.Id, messageId);
            }
            catch (MailSummaryUnavailableException ex)
            {
                fallbackReason ??= ex.Message;
            }
            catch (Exception ex)
            {
                fallbackReason ??= "摘要引擎出错（" + ex.GetType().Name + "）";
                _logger.LogWarning(ex, "摘要引擎失败：{Provider}，邮件 {Id}", provider.Id, messageId);
            }
        }

        throw new MailSummaryUnavailableException(
            "没有可用的摘要引擎：" + (fallbackReason ?? "请检查 设置 → 本地 AI 助手"));
    }

    private static string FormatTimeout(TimeSpan timeout) => timeout.TotalSeconds >= 1
        ? $"{(int)timeout.TotalSeconds} 秒"
        : $"{timeout.TotalMilliseconds:F0} 毫秒";

    private MailSummaryProviderStatus SafeStatus(IMailSummaryProvider provider)
    {
        try
        {
            // 状态查询是同步的轻量检查（文件是否存在、原生库能否加载），阻塞风险很低
            return provider.GetStatusAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取摘要引擎状态失败：{Provider}", provider.Id);
            return new MailSummaryProviderStatus
            {
                Available = false,
                UnavailableReason = "引擎状态读取失败"
            };
        }
    }

    /// <summary>输入正文 + 期望引擎的指纹：正文改动或换了引擎就重算。</summary>
    private static string Fingerprint(string text, string provider, bool localAiEnabled)
    {
        // 切换「本地 AI」开关会换引擎，缓存指纹要跟着变，否则会拿旧引擎的结果充数
        var bytes = Encoding.UTF8.GetBytes(provider + "\n" + (localAiEnabled ? "ai" : "rule") + "\n" + text);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed record CacheEntry(string Fingerprint, MailSummaryResult Result);
}
