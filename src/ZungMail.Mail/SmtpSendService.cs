using System.IO;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ZungMail.Mail;

/// <summary>SMTP 发送器（FR-07）。强制 SSL/TLS（NFR-S1），支持 HTML 正文、附件与回复线索。</summary>
public sealed class SmtpSendService : IMailSendService
{
    private const int DefaultTimeoutMs = 60_000;

    private readonly ILogger<SmtpSendService> _logger;

    public SmtpSendService(ILogger<SmtpSendService> logger)
    {
        _logger = logger;
    }

    public async Task SendAsync(Account account, OutboxItem item, CancellationToken ct = default)
    {
        if (!account.HasCompleteConfig) throw new InvalidOperationException("账户配置不完整，无法发送。");
        if (item.To.Count == 0 && item.Cc.Count == 0 && item.Bcc.Count == 0)
            throw new InvalidOperationException("收件人为空，无法发送。");

        // MimeMessage 持有附件 FileStream 与整封 MIME 缓冲：不释放会让句柄滞留到 GC
        using var message = BuildMessage(account, item);

        using var client = new SmtpClient { Timeout = DefaultTimeoutMs };
        try
        {
            await client
                .ConnectAsync(account.SmtpHost, account.SmtpPort, MailkitMapping.ToSocketOptions(account.SmtpSecure), ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(account.Username))
            {
                if (account.AuthKind == AuthKind.OAuth2)
                    await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, account.Secret), ct).ConfigureAwait(false);
                else
                    await client.AuthenticateAsync(account.Username, account.Secret, ct).ConfigureAwait(false);
            }

            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMTP 发送失败：{Host}:{Port}", account.SmtpHost, account.SmtpPort);
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(false, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* 断开失败无需上报 */ }
            }
            throw;
        }
    }

    /// <summary>把队列条目组装为 MIME 邮件（同时用于归档到「已发送」）。</summary>
    internal static MimeMessage BuildMessage(Account account, OutboxItem item)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.SenderDisplayName, account.Email));

        foreach (var address in item.To) message.To.Add(ToMailbox(address));
        foreach (var address in item.Cc) message.Cc.Add(ToMailbox(address));
        foreach (var address in item.Bcc) message.Bcc.Add(ToMailbox(address));

        message.Subject = item.Subject ?? string.Empty;

        // 使用入队时固定的 Message-ID：重试/归档保持同一标识，便于去重与人工核对。
        if (!string.IsNullOrWhiteSpace(item.MessageId))
        {
            message.MessageId = item.MessageId.Trim().Trim('<', '>');
        }

        if (!string.IsNullOrWhiteSpace(item.InReplyTo))
        {
            message.InReplyTo = item.InReplyTo.Trim().Trim('<', '>');
        }
        foreach (var reference in SplitMessageIds(item.References))
        {
            message.References.Add(reference);
        }

        var builder = new BodyBuilder();
        if (item.IsHtml)
        {
            // data: URI 在 Outlook 等客户端不渲染，发送前统一转成 multipart/related 内嵌资源
            var (html, images) = ComposeBodyBuilder.ExtractInlineImages(item.BodyHtml);
            builder.HtmlBody = html;

            var plain = HtmlTextExtractor.ToPlainText(html);
            if (plain.Length > 0) builder.TextBody = plain;

            foreach (var image in images)
            {
                builder.LinkedResources.Add(new MimePart(image.MediaType)
                {
                    Content = new MimeContent(new MemoryStream(image.Data)),
                    ContentId = image.ContentId,
                    ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
                    FileName = image.FileName
                });
            }
        }
        else
        {
            builder.TextBody = item.BodyHtml ?? string.Empty;
        }

        foreach (var path in item.AttachmentPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            builder.Attachments.Add(path);
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    private static MailboxAddress ToMailbox(EmailAddress address) =>
        new(address.Name ?? string.Empty, address.Address);

    private static IEnumerable<string> SplitMessageIds(string? references)
    {
        if (string.IsNullOrWhiteSpace(references)) yield break;
        foreach (var token in references.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var id = token.Trim().Trim('<', '>').Trim();
            if (id.Length > 0) yield return id;
        }
    }
}
