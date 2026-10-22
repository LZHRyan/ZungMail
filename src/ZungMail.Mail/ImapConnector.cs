using ZungMail.Core.Domain;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace ZungMail.Mail;

/// <summary>IMAP 连接建立（连接 + 认证）的共用入口，所有调用方负责 Dispose。</summary>
internal static class ImapConnector
{
    /// <summary>默认单条命令 / 读取超时。IDLE 长连接会显式传入更长的值。</summary>
    internal const int DefaultTimeoutMs = 60_000;

    public static async Task<ImapClient> ConnectAsync(Account account, CancellationToken ct, int timeoutMs = DefaultTimeoutMs)
    {
        var client = new ImapClient { Timeout = timeoutMs };
        try
        {
            await client
                .ConnectAsync(account.ImapHost, account.ImapPort, MailkitMapping.ToSocketOptions(account.ImapSecure), ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(account.Username))
            {
                try
                {
                    if (account.AuthKind == AuthKind.OAuth2)
                        await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, account.Secret), ct).ConfigureAwait(false);
                    else
                        await client.AuthenticateAsync(account.Username, account.Secret, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ImapProtocolException or ServiceNotConnectedException)
                {
                    // 部分服务商（263 等）在 AUTHENTICATE 被拒后会直接断开连接，
                    // MailKit 回退到 LOGIN 命令时便抛出协议异常而非 AuthenticationException。
                    // 若不归因到「登录被拒绝」，界面会显示成「无法连接服务器」这类误导性提示。
                    throw new MailKit.Security.AuthenticationException(
                        "服务器拒绝登录，请确认账号与授权码，并确认邮箱已开启 IMAP 服务。", ex);
                }
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
