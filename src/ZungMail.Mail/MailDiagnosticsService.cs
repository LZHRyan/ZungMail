using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace ZungMail.Mail;

/// <summary>分阶段诊断邮件收发链路，失败阶段直接给出可操作建议。</summary>
public sealed class MailDiagnosticsService : IMailDiagnosticsService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    private readonly IAccountRepository _accounts;
    private readonly IImapClientFactory _clientFactory;

    public MailDiagnosticsService(IAccountRepository accounts, IImapClientFactory clientFactory)
    {
        _accounts = accounts;
        _clientFactory = clientFactory;
    }

    public async Task<DiagnosticsReport> RunAsync(long accountId, CancellationToken ct = default)
    {
        var account = await _accounts.GetAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("账户不存在。");

        var report = new DiagnosticsReport
        {
            AccountId = account.Id,
            AccountEmail = account.Email,
            GeneratedUtc = DateTimeOffset.UtcNow
        };

        if (!account.HasCompleteConfig)
        {
            report.Steps.Add(new DiagnosticsStep("账户配置", false, "IMAP/SMTP 主机、端口或登录信息不完整。", 0));
            return report;
        }

        report.Steps.Add(await StepAsync("IMAP DNS 解析", async () =>
        {
            var addresses = await Dns.GetHostAddressesAsync(account.ImapHost!, ct).ConfigureAwait(false);
            return addresses.Length > 0
                ? (true, $"解析到 {addresses.Length} 个地址，首选 {addresses[0]}")
                : (false, "未解析到任何地址，请检查 DNS 或主机名拼写。");
        }).ConfigureAwait(false));

        report.Steps.Add(await StepAsync("IMAP TCP 连接", () =>
            ProbeTcpAsync(account.ImapHost!, account.ImapPort, ct)).ConfigureAwait(false));

        report.Steps.Add(await StepAsync("IMAP 登录", async () =>
        {
            using var session = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
            return session.IsConnected
                ? (true, $"登录成功（{(session.SupportsImap4Rev2 ? "IMAP4rev2" : "IMAP4rev1")}，MOVE={session.SupportsMove}）")
                : (false, "会话未建立。");
        }).ConfigureAwait(false));

        report.Steps.Add(await StepAsync("文件夹枚举", async () =>
        {
            using var session = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
            var folders = await session.GetFoldersAsync(ct).ConfigureAwait(false);
            return folders.Count > 0
                ? (true, $"读取到 {folders.Count} 个文件夹")
                : (false, "未读取到任何文件夹，可能是命名空间受限或账户未开启 IMAP。");
        }).ConfigureAwait(false));

        report.Steps.Add(await StepAsync("SMTP DNS 解析", async () =>
        {
            var addresses = await Dns.GetHostAddressesAsync(account.SmtpHost!, ct).ConfigureAwait(false);
            return addresses.Length > 0
                ? (true, $"解析到 {addresses.Length} 个地址，首选 {addresses[0]}")
                : (false, "未解析到任何地址，请检查 DNS 或主机名拼写。");
        }).ConfigureAwait(false));

        report.Steps.Add(await StepAsync("SMTP 登录", () => ProbeSmtpAsync(account, ct)).ConfigureAwait(false));

        return report;
    }

    private static async Task<(bool Success, string Detail)> ProbeTcpAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return (true, $"TCP {host}:{port} 可连通");
        }
        catch (OperationCanceledException)
        {
            return (false, $"连接 {host}:{port} 超时（{ConnectTimeout.TotalSeconds:0} 秒），可能是网络或防火墙拦截。");
        }
        catch (SocketException ex)
        {
            return (false, $"连接 {host}:{port} 失败：{ex.SocketErrorCode}。端口可能被封锁或服务未开放。");
        }
    }

    private static async Task<(bool Success, string Detail)> ProbeSmtpAsync(Account account, CancellationToken ct)
    {
        using var smtp = new SmtpClient { Timeout = (int)ConnectTimeout.TotalMilliseconds };
        try
        {
            await smtp.ConnectAsync(account.SmtpHost!, account.SmtpPort, MailkitMapping.ToSocketOptions(account.SmtpSecure), ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(account.Username))
            {
                if (account.AuthKind == AuthKind.OAuth2)
                    await smtp.AuthenticateAsync(new SaslMechanismOAuth2(account.Username, account.Secret), ct).ConfigureAwait(false);
                else
                    await smtp.AuthenticateAsync(account.Username, account.Secret, ct).ConfigureAwait(false);
            }

            await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
            return (true, $"SMTP {account.SmtpHost}:{account.SmtpPort} 认证成功");
        }
        catch (Exception ex)
        {
            var hint = ex is MailKit.Security.AuthenticationException or SmtpCommandException
                ? "SMTP 认证失败，请确认使用的是授权码而非登录密码："
                : "SMTP 连接失败：";
            return (false, hint + ex.Message);
        }
    }

    private static async Task<DiagnosticsStep> StepAsync(
        string name, Func<Task<(bool Success, string Detail)>> probe)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (success, detail) = await probe().ConfigureAwait(false);
            return new DiagnosticsStep(name, success, detail, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return new DiagnosticsStep(name, false, ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }
}
