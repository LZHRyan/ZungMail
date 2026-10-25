using System.Globalization;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Microsoft.Extensions.Logging;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// 一次性回填：把历史邮件正文里的 base64 内嵌图（<c>data:image/…;base64,…</c>）落盘，
/// 并把正文改写成 <c>file:///</c> 引用。
///
/// 新邮件在收信时（<c>MimeMapper</c>）已经这样做；这个回填只处理升级前入库的老邮件——
/// 实测一封 8.3MB 的水单邮件正文 6.77MB、57 张 base64 图，留在库里既占空间
/// 又让每次打开的读库、搜索与渲染都变慢。
///
/// <para>
/// 完成标记写进数据库 <c>meta</c> 表（<see cref="IAppMetadataStore"/>），只跑一次；
/// 扫描用 LIKE 全表一次，结果按批处理，异常的邮件跳过并记日志，绝不阻塞首屏。
/// </para>
/// </summary>
public sealed class InlineDataImageBackfillService
{
    /// <summary>完成标记的键（跟着数据库走，换数据目录也跟着搬）。</summary>
    public const string MetaKey = "backfill.inline-data-images.v1";

    /// <summary>单次回填最多处理多少封（剩余的下次启动继续，避免一次跑几十分钟）。</summary>
    public const int MaxMessagesPerRun = 200;

    private readonly IMessageRepository _messages;
    private readonly IAppPaths _paths;
    private readonly IAppMetadataStore _meta;
    private readonly ILogger<InlineDataImageBackfillService> _logger;

    /// <summary>单飞：启动与补扫各触发一次时不重复跑。</summary>
    private int _running;

    public InlineDataImageBackfillService(
        IMessageRepository messages,
        IAppPaths paths,
        IAppMetadataStore meta,
        ILogger<InlineDataImageBackfillService> logger)
    {
        _messages = messages;
        _paths = paths;
        _meta = meta;
        _logger = logger;
    }

    /// <summary>
    /// 执行回填，返回本次处理的邮件数与正文压缩的总字符数。已标记完成时直接返回 (0, 0)。
    /// </summary>
    public async Task<(int Messages, long SavedChars)> RunAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return (0, 0);

        try
        {
            if (await _meta.HasAsync(MetaKey, ct).ConfigureAwait(false)) return (0, 0);

            var ids = await _messages.GetInlineDataImageMessageIdsAsync(MaxMessagesPerRun, ct).ConfigureAwait(false);
            if (ids.Count == 0)
            {
                await MarkDoneAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("内嵌图片回填：没有需要处理的历史邮件");
                return (0, 0);
            }

            var processed = 0;
            var saved = 0L;

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (await BackfillOneAsync(id, ct).ConfigureAwait(false) is { } delta)
                    {
                        processed++;
                        saved += delta;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单封失败不影响其余：下次启动会重新扫到它
                    _logger.LogWarning(ex, "内嵌图片回填失败：邮件 {Id}", id);
                }
            }

            // 一次没跑完（达到上限）就先不标记完成，下次启动继续
            if (ids.Count < MaxMessagesPerRun) await MarkDoneAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "内嵌图片回填：处理 {Count} 封，正文减少 {Saved:N0} 字符（约 {Mb:F1} MB）",
                processed, saved, saved / 1024d / 1024d);

            return (processed, saved);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private Task MarkDoneAsync(CancellationToken ct)
        => _meta.SetAsync(MetaKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ct);

    /// <summary>处理一封邮件；没有可落盘的图片时返回 null。</summary>
    private async Task<long?> BackfillOneAsync(long id, CancellationToken ct)
    {
        var detail = await _messages.GetDetailAsync(id, ct).ConfigureAwait(false);
        if (detail?.BodyHtml is not { Length: > 0 } html) return null;
        if (!MailBodyDataImageExtractor.HasDataImages(html)) return null;

        var directory = Path.Combine(
            _paths.EmlDirectory,
            detail.AccountId.ToString(CultureInfo.InvariantCulture),
            detail.Id.ToString(CultureInfo.InvariantCulture));

        var attachments = detail.Attachments.ToList();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in attachments)
        {
            if (!string.IsNullOrWhiteSpace(existing.FileName)) usedNames.Add(existing.FileName);
        }

        var rewritten = MailBodyDataImageExtractor.ReplaceDataImages(html, (mimeType, bytes, index) =>
        {
            try
            {
                Directory.CreateDirectory(directory);

                var extension = MailBodyDataImageExtractor.ExtensionFor(mimeType) ?? ".png";
                var name = $"inline-{index}{extension}";
                var counter = 1;
                while (!usedNames.Add(name))
                {
                    name = $"inline-{index}-{++counter}{extension}";
                }

                var path = Path.Combine(directory, name);
                File.WriteAllBytes(path, bytes);

                attachments.Add(new AttachmentInfo
                {
                    EmailId = detail.Id,
                    FileName = name,
                    ContentType = mimeType,
                    Size = bytes.Length,
                    IsInline = true,
                    LocalPath = path
                });

                return new Uri(path).AbsoluteUri;
            }
            catch (Exception ex)
            {
                // 单张图落盘失败：保持 data: 原样，正文仍可显示
                _logger.LogDebug(ex, "内嵌图片落盘失败：邮件 {Id} 第 {Index} 张", detail.Id, index);
                return null;
            }
        });

        if (rewritten.Length >= html.Length) return null;

        await _messages.SaveBodyAsync(id, rewritten, detail.BodyText ?? string.Empty, attachments, ct)
            .ConfigureAwait(false);

        return html.Length - rewritten.Length;
    }
}
