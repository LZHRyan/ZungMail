using ZungMail.Core.Domain;
using MimeKit;

namespace ZungMail.Mail;

/// <summary>
/// 把本地库里的邮件重建为 .eml 文件，供「作为附件转发」使用。
/// <para>
/// 优先按库里的字段重建（主题 / 收发件人 / 时间 / Message-ID / 正文 / 附件文件），
/// 这样即使原始 MIME 不在本地（早期版本没有落 .eml）也能得到一个可被 Outlook / Foxmail 打开的邮件。
/// 正文尚未下载时返回 null，由界面提示用户先打开一次该邮件。
/// </para>
/// </summary>
public static class MailEmlExporter
{
    /// <summary>转发附件落在 <paramref name="emlDirectory"/> 下的 forward 子目录，返回值即 .eml 的完整路径。</summary>
    public static async Task<string?> ExportAsync(
        EmailDetail detail, string emlDirectory, CancellationToken ct = default)
    {
        if (!detail.HasBody) return null;

        var directory = Path.Combine(emlDirectory, "forward", detail.Id.ToString());
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, BuildFileName(detail));
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true))
        {
            await Build(detail).WriteToAsync(stream, ct).ConfigureAwait(false);
        }

        return path;
    }

    /// <summary>按库里的字段拼一封 RFC 822 邮件（发送时间里保留原时间，便于收件人看出原始邮件时间）。</summary>
    public static MimeMessage Build(EmailDetail detail)
    {
        var message = new MimeMessage();

        if (!string.IsNullOrWhiteSpace(detail.FromEmail))
        {
            message.From.Add(new MailboxAddress(detail.FromName ?? string.Empty, detail.FromEmail));
        }

        foreach (var address in detail.To) AddAddress(message.To, address);
        foreach (var address in detail.Cc) AddAddress(message.Cc, address);
        foreach (var address in detail.Bcc) AddAddress(message.Bcc, address);

        if (detail.ReplyTo is { Address.Length: > 0 } replyTo)
        {
            message.ReplyTo.Add(new MailboxAddress(replyTo.Name ?? string.Empty, replyTo.Address));
        }

        message.Subject = detail.Subject ?? string.Empty;
        message.Date = detail.DateUtc;

        if (!string.IsNullOrWhiteSpace(detail.MessageId)) message.MessageId = detail.MessageId;
        foreach (var reference in detail.References.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            message.References.Add(reference);
        }

        var builder = new BodyBuilder
        {
            TextBody = string.IsNullOrWhiteSpace(detail.BodyText) ? null : detail.BodyText,
            HtmlBody = string.IsNullOrWhiteSpace(detail.BodyHtml) ? null : detail.BodyHtml
        };

        // 正文内嵌图片：库里的 html 引用的是本地 file:///（读的时候靠虚拟主机映射），
        // 打成 .eml 后必须变成 multipart/related 里的 cid:，否则收件人看到的是一堆破图。
        var linked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attachment in detail.Attachments)
        {
            if (!attachment.IsInline) continue;
            if (string.IsNullOrWhiteSpace(attachment.LocalPath) || !File.Exists(attachment.LocalPath)) continue;

            var resource = builder.LinkedResources.Add(
                attachment.FileName,
                File.ReadAllBytes(attachment.LocalPath),
                ContentType.Parse(string.IsNullOrWhiteSpace(attachment.ContentType)
                    ? "application/octet-stream"
                    : attachment.ContentType));

            resource.ContentId = "forward-" + Guid.NewGuid().ToString("N") + "@zungmail";
            linked[NormalizePath(attachment.LocalPath)] = resource.ContentId;
        }

        if (linked.Count > 0 && !string.IsNullOrWhiteSpace(builder.HtmlBody))
        {
            builder.HtmlBody = LocalImageRegex.Replace(builder.HtmlBody, match =>
            {
                var decoded = Uri.UnescapeDataString(match.Value);
                var local = decoded.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
                    ? decoded[8..]
                    : decoded.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? decoded[7..] : decoded;
                return linked.TryGetValue(NormalizePath(local), out var contentId) ? "cid:" + contentId : match.Value;
            });
        }

        // 普通附件原样带上；内嵌图片已作为 related 资源，不重复附加
        foreach (var attachment in detail.Attachments)
        {
            if (attachment.IsInline) continue;
            if (string.IsNullOrWhiteSpace(attachment.LocalPath) || !File.Exists(attachment.LocalPath)) continue;

            builder.Attachments.Add(
                attachment.FileName,
                File.ReadAllBytes(attachment.LocalPath),
                ContentType.Parse(string.IsNullOrWhiteSpace(attachment.ContentType)
                    ? "application/octet-stream"
                    : attachment.ContentType));
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    /// <summary>匹配正文里的本地图片地址（file:///C:/…），用于替换成 cid:。</summary>
    private static readonly System.Text.RegularExpressions.Regex LocalImageRegex = new(
        "file:///[^\"'\\s>]+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string NormalizePath(string path)
        => path.Replace('/', '\\').Trim();

    private static void AddAddress(InternetAddressList list, EmailAddress address)
    {
        if (string.IsNullOrWhiteSpace(address.Address)) return;
        list.Add(new MailboxAddress(address.Name ?? string.Empty, address.Address));
    }

    /// <summary>附件文件名用原主题，去掉 Windows 不允许的字符；空主题回落到「转发的邮件」。</summary>
    private static string BuildFileName(EmailDetail detail)
    {
        var subject = (detail.Subject ?? string.Empty).Trim();
        if (subject.Length == 0) subject = "转发的邮件";

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(subject.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim(' ', '.');
        if (safe.Length == 0) safe = "转发的邮件";
        if (safe.Length > 80) safe = safe[..80];

        return safe + ".eml";
    }
}
