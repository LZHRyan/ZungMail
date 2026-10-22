using System.Globalization;
using System.Text;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using MailKit;
using MimeKit;

namespace ZungMail.Mail;

/// <summary>IMAP 抓取结果 → 领域模型：邮件头摘要映射与正文/附件落盘。</summary>
internal static class MimeMapper
{
    /// <summary>同步邮件头所需字段（NFR-P4：默认只拉邮件头）。</summary>
    public const MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId |
        MessageSummaryItems.Flags |
        MessageSummaryItems.Size |
        MessageSummaryItems.Envelope |
        MessageSummaryItems.InternalDate |
        MessageSummaryItems.BodyStructure |
        MessageSummaryItems.PreviewText;

    /// <summary>
    /// 不含预览文本的邮件头字段。部分服务商（263 等）对预览文本的 FETCH 响应格式不合法，
    /// MailKit 解析失败会判连接损坏，此时降级为不拉预览，保证邮件头仍能同步。
    /// </summary>
    public const MessageSummaryItems SummaryItemsWithoutPreview = SummaryItems & ~MessageSummaryItems.PreviewText;

    /// <summary>
    /// 最小可用邮件头字段。少数服务商对某些邮件的 BODYSTRUCTURE 响应不合法（如 263 返回
    /// <c>Syntax error in BODYSTRUCTURE. Unexpected token: NIL</c>），去掉后仍能同步主题/收发件人/时间等，
    /// 仅失去「是否有附件」的判定（下载正文时仍可识别）。
    /// </summary>
    public const MessageSummaryItems SummaryItemsMinimal = SummaryItemsWithoutPreview & ~MessageSummaryItems.BodyStructure;

    /// <summary>会话归并所需的额外头（Envelope 不含 References）。</summary>
    public static readonly IReadOnlyList<HeaderId> ExtraHeaders = new[] { HeaderId.References, HeaderId.DispositionNotificationTo };

    // ---------------- 邮件头 → 摘要 ----------------

    public static EmailDetail ToDetail(long accountId, long folderId, uint uid, RemoteMessageSummary summary)
    {
        var envelope = summary.Envelope;
        var from = FirstMailbox(envelope?.From);
        var replyTo = FirstMailbox(envelope?.ReplyTo);

        var detail = new EmailDetail
        {
            AccountId = accountId,
            FolderId = folderId,
            Uid = uid,
            MessageId = (envelope?.MessageId ?? string.Empty).Trim(),
            // 部分服务器的 ENVELOPE.Subject 未自动解析 RFC 2047 encoded-word，需在入库前兜底解码。
            Subject = MimeHeaderDecoder.DecodeSubject(envelope?.Subject),
            FromName = from?.Name ?? string.Empty,
            FromEmail = from?.Address ?? string.Empty,
            DateUtc = envelope?.Date ?? summary.InternalDate ?? DateTimeOffset.UtcNow,
            Preview = BuildPreview(summary.PreviewText),
            Flags = MailkitMapping.ToLocalFlags(summary.Flags),
            Size = summary.Size ?? 0,
            HasAttachment = summary.HasAttachment,
            To = ToAddresses(envelope?.To),
            Cc = ToAddresses(envelope?.Cc),
            Bcc = ToAddresses(envelope?.Bcc)
        };

        if (replyTo is not null && !string.IsNullOrWhiteSpace(replyTo.Address))
        {
            detail.ReplyTo = new EmailAddress(replyTo.Address, replyTo.Name ?? string.Empty);
        }

        detail.References = ExtractMessageIds(summary.References);
        detail.MdnTo = (summary.MdnTo ?? string.Empty).Trim();
        detail.ThreadId = ThreadKeyBuilder.Build(detail.MessageId, detail.References, detail.Subject);
        return detail;
    }

    // ---------------- 正文与附件 ----------------

    /// <summary>下载完整邮件并把正文、附件写入本地（附件落在 <see cref="IAppPaths.EmlDirectory"/>）。</summary>
    /// <param name="saveAttachments">
    /// 为 false 时不落盘附件（账户策略「仅收取正文」）：附件条目仍会记录名称 / 类型 / 大小，
    /// 但 <c>LocalPath</c> 为空、<c>IsDownloaded</c> 为 false，等用户单独点「下载附件」再取。
    /// 内嵌图片属于正文的一部分，这一档仍然落盘，否则正文会全是破图。
    /// </param>
    public static async Task<(string Html, string Text, List<AttachmentInfo> Attachments)> ExtractBodyAsync(
        MimeMessage message, IAppPaths paths, EmailDetail detail, CancellationToken ct, bool saveAttachments = true)
    {
        var text = message.TextBody ?? string.Empty;
        var html = message.HtmlBody ?? string.Empty;

        var attachments = new List<AttachmentInfo>();
        var inlineUris = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(
            paths.EmlDirectory,
            detail.AccountId.ToString(CultureInfo.InvariantCulture),
            detail.Id.ToString(CultureInfo.InvariantCulture));
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        // 正文实体本身不是附件；multipart/related 中的内嵌图片必须一并落盘，
        // 否则正文里的 cid: 引用无法显示（MimeMessage.Attachments 不含这类关联部件）。
        foreach (var part in EnumerateParts(message.Body))
        {
            if (part.Content is null || IsBodyText(part)) continue;

            index++;
            var isInline = !(part.ContentDisposition?.IsAttachment ?? false) && !string.IsNullOrEmpty(part.ContentId);
            var fileName = UniqueFileName(part.FileName, index, usedNames, part.ContentType?.MimeType, isInline);
            string? localPath = null;

            // 「仅收取正文」：普通附件不落盘（内嵌图片仍要写，否则正文渲染不出图）
            var shouldSave = saveAttachments || isInline;

            if (shouldSave)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    localPath = Path.Combine(directory, fileName);
                    await using (var stream = File.Create(localPath))
                    {
                        await part.Content.DecodeToAsync(stream, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    localPath = null;
                }
            }

            long size = 0;
            if (localPath is not null)
            {
                try { size = new FileInfo(localPath).Length; }
                catch (IOException) { size = 0; }
            }
            else if (!shouldSave && part.Content.Stream is { } encodedStream)
            {
                // 未落盘时用编码前的字节数估算大小，列表里仍能显示「2.3 MB」
                try { size = encodedStream.Length; }
                catch (Exception) { size = 0; }
            }

            if (isInline && localPath is not null)
            {
                inlineUris[TrimBrackets(part.ContentId!)] = new Uri(localPath).AbsoluteUri;
            }

            attachments.Add(new AttachmentInfo
            {
                EmailId = detail.Id,
                FileName = fileName,
                ContentType = part.ContentType?.MimeType ?? "application/octet-stream",
                Size = size,
                IsInline = isInline,
                LocalPath = localPath
            });
        }

        // 内嵌图片：把 cid: 引用改写到本地文件，便于界面直接渲染 HTML。
        foreach (var (cid, uri) in inlineUris)
        {
            html = html.Replace("cid:" + cid, uri, StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(html))
            text = HtmlTextExtractor.ToPlainText(html);
        if (string.IsNullOrEmpty(html) && !string.IsNullOrEmpty(text))
            html = HtmlTextExtractor.ToHtml(text);

        return (html, text, attachments);
    }

    // ---------------- 辅助 ----------------

    private static MailboxAddress? FirstMailbox(InternetAddressList? list)
    {
        if (list is null) return null;
        foreach (var mailbox in list.Mailboxes)
        {
            if (!string.IsNullOrWhiteSpace(mailbox.Address)) return mailbox;
        }
        return null;
    }

    private static List<EmailAddress> ToAddresses(InternetAddressList? list)
    {
        var result = new List<EmailAddress>();
        if (list is null) return result;
        foreach (var mailbox in list.Mailboxes)
        {
            if (!string.IsNullOrWhiteSpace(mailbox.Address))
                result.Add(new EmailAddress(mailbox.Address, mailbox.Name ?? string.Empty));
        }
        return result;
    }

    /// <summary>服务器返回的预览片段可能直接来自 HTML 正文，遇到标签先转纯文本。</summary>
    private static string BuildPreview(string? preview)
    {
        var raw = preview ?? string.Empty;
        if (raw.Contains('<') && raw.Contains('>')) raw = HtmlTextExtractor.ToPlainText(raw);
        return DisplayFormat.Preview(raw, 160);
    }

    private static List<string> ExtractMessageIds(string? references)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(references)) return result;
        foreach (var token in references.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var id = TrimBrackets(token);
            if (id.Length > 0) result.Add(id);
        }
        return result;
    }

    private static string TrimBrackets(string value) => value.Trim().Trim('<', '>').Trim();

    /// <summary>判断是否为正文文本实体（无 ContentId、未标记为附件），这类实体不落盘。</summary>
    private static bool IsBodyText(MimePart part)
        => part is TextPart
           && string.IsNullOrEmpty(part.ContentId)
           && !(part.ContentDisposition?.IsAttachment ?? false);

    /// <summary>递归枚举所有叶子 MimePart；嵌套 message/rfc822 不展开。</summary>
    private static IEnumerable<MimePart> EnumerateParts(MimeEntity? entity)
    {
        switch (entity)
        {
            case Multipart multipart:
                foreach (var child in multipart)
                {
                    foreach (var part in EnumerateParts(child)) yield return part;
                }
                break;

            case MessagePart:
                break;

            case MimePart part:
                yield return part;
                break;
        }
    }

    private static string UniqueFileName(string? name, int index, HashSet<string> used, string? mimeType, bool isInline)
    {
        var candidate = SanitizeFileName(name, index);
        if (isInline) candidate = EnsureExtension(candidate, mimeType);
        if (used.Add(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);
        var counter = 1;
        string unique;
        do
        {
            unique = $"{stem}-{++counter}{extension}";
        }
        while (!used.Add(unique));
        return unique;
    }

    /// <summary>内嵌图片常无文件名，按 MIME 类型补扩展名，虚拟主机才能正确识别图片。</summary>
    private static string EnsureExtension(string fileName, string? mimeType)
    {
        if (Path.HasExtension(fileName) || string.IsNullOrWhiteSpace(mimeType)) return fileName;

        var extension = mimeType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" or "image/x-ms-bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            "image/tiff" => ".tif",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            _ => null
        };

        return extension is null ? fileName : fileName + extension;
    }

    private static string SanitizeFileName(string? name, int index)
    {
        var raw = string.IsNullOrWhiteSpace(name) ? $"attachment-{index}" : name!;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var result = sb.ToString().Trim().Trim('.');
        if (result.Length == 0) result = $"attachment-{index}";
        if (result.Length > 120) result = result[..120];
        return result;
    }
}
