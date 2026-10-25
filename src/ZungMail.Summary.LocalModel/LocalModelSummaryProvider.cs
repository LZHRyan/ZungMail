using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;

namespace ZungMail.Summary.LocalModel;

/// <summary>
/// 本地小模型引擎：模型文件由 <see cref="ISummaryModelStore"/> 管理，推理由 <see cref="ILlamaInference"/> 完成，
/// 本类只负责「检查可用性 → 拼提示词 → 调推理 → 解析三段结构」。
/// </summary>
public sealed class LocalModelSummaryProvider : IMailSummaryProvider
{
    public const string ProviderId = "local-model";

    /// <summary>
    /// 分段推理的总时间预算：0.5B 在 CPU 上处理 3000 字约 4–6 秒，
    /// 超过这个预算就先停（返回已覆盖的前几段）并在卡片上说明，而不是等编排层的 25 秒超时整封降级成规则抽取。
    /// </summary>
    private static readonly TimeSpan ChunkTimeBudget = TimeSpan.FromSeconds(12);

    private readonly ISummaryModelStore _modelStore;
    private readonly ILlamaInference _inference;
    private readonly ILogger<LocalModelSummaryProvider>? _logger;

    public LocalModelSummaryProvider(
        ISummaryModelStore modelStore,
        ILlamaInference inference,
        ILogger<LocalModelSummaryProvider>? logger = null)
    {
        _modelStore = modelStore;
        _inference = inference;
        _logger = logger;
    }

    public string Id => ProviderId;

    public string DisplayName => "本地模型";

    public Task<MailSummaryProviderStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var status = _modelStore.GetStatus();

        if (status.State == SummaryModelState.Ready)
        {
            // 推理失败不再永久禁用引擎：失败时卸载模型，下一次点击重新加载即可（避免「只能重启」）
            return Task.FromResult(new MailSummaryProviderStatus { Available = true });
        }

        if (status.State == SummaryModelState.Downloading)
        {
            return Task.FromResult(new MailSummaryProviderStatus
            {
                Available = false,
                NeedsModel = true,
                UnavailableReason = "摘要模型正在下载"
            });
        }

        var reason = status.State == SummaryModelState.Invalid
            ? "摘要模型文件不可用：" + (status.Detail ?? "校验失败")
            : "摘要模型尚未下载（约 400 MB，可在 设置 → 本地 AI 助手 下载）";

        return Task.FromResult(new MailSummaryProviderStatus
        {
            Available = false,
            NeedsModel = status.State != SummaryModelState.Invalid,
            UnavailableReason = reason
        });
    }

    public async Task<MailSummaryResult> SummarizeAsync(
        MailSummaryRequest request,
        IProgress<MailSummaryProgress>? progress = null,
        CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (!status.Available)
        {
            throw new MailSummaryUnavailableException(status.UnavailableReason ?? "本地模型不可用");
        }

        var plan = MailSummaryChunker.Split(request.InputText, request.ChunkChars, MailSummarySettings.MaxChunks);
        if (plan.Count == 0)
        {
            throw new MailSummaryUnavailableException("没有可总结的正文。");
        }

        var started = Stopwatch.StartNew();
        var processedChars = 0;
        var processedChunks = 0;
        var stoppedByBudget = false;
        var modelBullets = new List<string>();
        var modelActions = new List<string>();
        var modelReferences = new List<string>();
        var raw = new System.Text.StringBuilder();

        try
        {
            for (var index = 0; index < plan.Count; index++)
            {
                ct.ThrowIfCancellationRequested();

                // 已经跑够预算且至少完成一段：不再开新段，把结果标记成「只覆盖前 N 字」
                if (index > 0 && started.Elapsed >= ChunkTimeBudget)
                {
                    stoppedByBudget = true;
                    break;
                }

                var chunk = plan.Chunks[index];

                if (plan.IsMultiChunk)
                {
                    progress?.Report(new MailSummaryProgress(
                        $"正在总结第 {chunk.Index}/{plan.Count} 段",
                        (double)index / plan.Count,
                        $"共 {plan.CoveredChars} 字"));
                }

                // 每一段都当独立请求：提示词里的「正文」就是这一段
                var chunkRequest = request with { InputText = chunk.Text };
                var prompt = MailSummaryPrompt.Build(chunkRequest);

                // 分段时每段只需产出少量候选要点（合并后还会去重），缩短单段耗时
                var maxTokens = plan.IsMultiChunk
                    ? Math.Min(MailSummaryPrompt.MaxTokensFor(request.Length), 140)
                    : MailSummaryPrompt.MaxTokensFor(request.Length);

                var chunkRaw = await _inference.CompleteAsync(
                    prompt,
                    maxTokens,
                    MailSummaryPrompt.TemperatureFor(request.Length),
                    progress: null,
                    ct).ConfigureAwait(false);

                raw.AppendLine(chunkRaw.Trim());

                processedChars += chunk.Text.Length;
                processedChunks++;
                var parsed = MailSummaryTextParser.Parse(chunkRaw);
                modelBullets.AddRange(parsed.Bullets);
                modelActions.AddRange(parsed.Actions);
                modelReferences.AddRange(parsed.References);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 卸载权重，下一次点击重新加载一个干净的上下文；错误文案要说明「可以直接重试」
            _inference.Unload();
            _logger?.LogWarning(ex, "本地模型推理失败：邮件 {Id}", request.MessageId);
            throw new MailSummaryUnavailableException(
                "本地模型推理失败（" + ex.GetType().Name + "），已重置模型，可以直接重试", ex);
        }

        started.Stop();

        // 小模型的价值在「概括」，不在「记得住数字」：单号与时效一律以**全文**的确定性规则抽取为准，
        // 这样分段也不会漏掉尾部出现的箱号/截止时间，数字也不会被模型改写。
        var rule = await new RuleBasedMailSummarizer()
            .SummarizeAsync(request, progress: null, ct)
            .ConfigureAwait(false);

        var bulletLimit = request.Length switch
        {
            MailSummaryLength.Short => 3,
            MailSummaryLength.Detailed => 5,
            _ => 4
        };

        var bullets = Distinct(modelBullets, bulletLimit);
        if (bullets.Count == 0) bullets = rule.Bullets;

        var references = rule.References.Count > 0 ? rule.References : Distinct(modelReferences, 8);

        var actions = Distinct(rule.Actions.Concat(modelActions), max: 6);

        progress?.Report(new MailSummaryProgress("完成", 1));

        return new MailSummaryResult
        {
            MessageId = request.MessageId,
            ProviderId = Id,
            ProviderName = DisplayName,
            Bullets = bullets,
            Actions = actions,
            References = references,
            RawText = raw.ToString().Trim(),
            Truncated = request.Truncated || !plan.Covered || stoppedByBudget,
            InputChars = stoppedByBudget ? processedChars : plan.CoveredChars,
            ChunkCount = Math.Max(1, processedChunks),
            ElapsedMs = started.ElapsedMilliseconds
        };
    }

    /// <summary>去重并截断：小模型会把同一句翻来覆去写几遍，只差标点的也算同一条。</summary>
    private static IReadOnlyList<string> Distinct(IEnumerable<string> items, int max)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var item in items)
        {
            var text = item.Trim();
            if (text.Length == 0) continue;

            var key = new string(text
                .Where(ch => !char.IsWhiteSpace(ch) && ch is not ('，' or '。' or '、' or ',' or '.' or '；' or ';'))
                .ToArray());
            if (!seen.Add(key)) continue;

            result.Add(text);
            if (result.Count >= max) break;
        }

        return result;
    }

    /// <summary>模型更换或重新检测后卸载权重，下次推理重新加载。</summary>
    public void ResetState() => _inference.Unload();
}
