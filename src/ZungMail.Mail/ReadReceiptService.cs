using System.Text;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ZungMail.Mail;

/// <summary>
/// 阅读收条（MDN）发送器：按 RFC 8098 组装 multipart/report(disposition-notification)，
/// 通过与普通发信相同的 SMTP 通道发送，并带 Auto-Submitted 头防止回执循环。
/// </summary>
public sealed class ReadReceiptService : IReadReceiptService
{
    private const int DefaultTimeoutMs = 30_000;

    private readonly ILogger<ReadReceiptService> _logger;

    public ReadReceiptService(ILogger<ReadReceiptService> logger) => _logger = logger;

    public async Task SendAsync(Account account, EmailDetail original, CancellationToken ct = default)
    {
        if (!account.HasCompleteConfig) throw new InvalidOperationException("账户配置不完整，无法发送回执。");
        if (string.IsNullOrWhiteSpace(original.MdnTo)) throw new InvalidOperationException("该邮件未请求阅读收条。");

        using var message = BuildReceipt(account, original);

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
            _logger.LogWarning(ex, "发送阅读回执失败：{Host}:{Port}", account.SmtpHost, account.SmtpPort);
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(false, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* 断开失败无需上报 */ }
            }
            throw;
        }
    }

    /// <summary>组装 MDN 报文（独立出来便于单元测试）。</summary>
    public static MimeMessage BuildReceipt(Account account, EmailDetail original)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.SenderDisplayName, account.Email));
        message.To.Add(ParseMdnTarget(original.MdnTo, account.Email));
        message.Subject = $"已读回执：{original.Subject}";
        message.Headers.Add("Auto-Submitted", "auto-replied");

        var originalId = (original.MessageId ?? string.Empty).Trim().Trim('<', '>');
        if (originalId.Length > 0)
        {
            // 回执与被读邮件建立关联，便于对方客户端归并
            message.InReplyTo = originalId;
            message.References.Add(originalId);
        }

        var report = new Multipart("report");
        report.ContentType.Parameters["report-type"] = "disposition-notification";

        report.Add(new TextPart("plain")
        {
            Text = $"您的邮件《{original.Subject}》已于 {DateTimeOffset.Now:yyyy-MM-dd HH:mm} 被阅读。\n" +
                   "此回执由 ZungMail 自动发送。\n"
        });

        var fields = new StringBuilder()
            .AppendLine("Reporting-UA: ZungMail")
            .AppendLine($"Final-Recipient: rfc822; {account.Email}")
            .AppendLine($"Original-Message-ID: {(originalId.Length > 0 ? $"<{originalId}>" : "<unknown>")}")
            .AppendLine("Disposition: manual-action/MDN-sent-manually; displayed")
            .ToString();

        var machine = new MimePart("message", "disposition-notification")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(fields)))
        };
        machine.ContentDisposition = new ContentDisposition(ContentDisposition.Inline);
        report.Add(machine);

        message.Body = report;
        return message;
    }

    /// <summary>Disposition-Notification-To 可能是「名字 &lt;a@b&gt;」，解析失败时回退账户地址。</summary>
    private static MailboxAddress ParseMdnTarget(string? mdnTo, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(mdnTo))
        {
            try
            {
                // 必须含 @，否则视为非法地址（MimeKit 会容忍无域名的本地部分）
                if (InternetAddressList.TryParse(mdnTo, out var parsed) && parsed.Count > 0
                    && parsed[0] is MailboxAddress mailbox
                    && !string.IsNullOrWhiteSpace(mailbox.Address)
                    && mailbox.Address.Contains('@'))
                {
                    return new MailboxAddress(mailbox.Name ?? string.Empty, mailbox.Address);
                }
            }
            catch (ParseException)
            {
                // 头值不规范时回退
            }
        }

        return new MailboxAddress(string.Empty, fallback);
    }
}
