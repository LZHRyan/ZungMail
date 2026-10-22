using ZungMail.Core.Domain;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;

namespace ZungMail.Mail;

/// <summary>
/// 收件箱长连接监听：走 IMAP IDLE，服务端一有新的 EXISTS 就回调，用来做「秒级收信」。
/// 与定时轮询并存——IDLE 掉线或服务器不支持时，轮询仍然兜底。
/// </summary>
public interface IImapIdleWatcher
{
    /// <summary>
    /// 监听指定账户的收件箱，直到 <paramref name="ct"/> 取消或连接异常。
    /// 返回 <c>true</c> 表示服务器支持 IDLE（本次退出是掉线/取消，可以重连再听）；
    /// 返回 <c>false</c> 表示服务器不支持 IDLE，调用方不应再做重试（交给定时轮询）。
    /// </summary>
    Task<bool> WatchInboxAsync(
        Account account, Func<CancellationToken, Task> onInboxChanged, CancellationToken ct = default);
}

/// <summary>基于 MailKit 的 IDLE 实现。</summary>
public sealed class MailKitImapIdleWatcher : IImapIdleWatcher
{
    /// <summary>
    /// 单次 IDLE 的最长时间：RFC 2177 要求客户端至少 29 分钟内重发一次 IDLE，
    /// 中间设备也常在 30 分钟处掐连接，这里用 9 分钟留足余量。
    /// </summary>
    private static readonly TimeSpan IdleCycle = TimeSpan.FromMinutes(9);

    private readonly ILogger<MailKitImapIdleWatcher> _logger;

    public MailKitImapIdleWatcher(ILogger<MailKitImapIdleWatcher> logger) => _logger = logger;

    public async Task<bool> WatchInboxAsync(
        Account account, Func<CancellationToken, Task> onInboxChanged, CancellationToken ct = default)
    {
        // IDLE 期间不会有数据回来，读超时必须放宽到大于单次 IDLE 周期
        using var client = await ImapConnector
            .ConnectAsync(account, ct, timeoutMs: (int)IdleCycle.TotalMilliseconds + 60_000)
            .ConfigureAwait(false);

        if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
        {
            _logger.LogInformation("账户 {Account} 的服务器不支持 IDLE，收信走定时轮询", account.Name);
            return false;
        }

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        _logger.LogInformation("账户 {Account} 已建立 IDLE 监听（收件箱 {Count} 封）", account.Name, inbox.Count);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var changed = 0;
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(ct);

                // 收到新邮件立刻结束本次 IDLE（读 IMMEDIATE 数据后再重新进入），
                // 另外到点也要主动结束一次：RFC 2177 要求 29 分钟内重发 IDLE，中间设备也常在 30 分钟处掐连接。
                cycle.CancelAfter(IdleCycle);

                void OnCountChanged(object? sender, EventArgs e)
                {
                    if (Interlocked.Exchange(ref changed, 1) == 0 && !cycle.IsCancellationRequested) cycle.Cancel();
                }

                inbox.CountChanged += OnCountChanged;
                try
                {
                    await client.IdleAsync(cycle.Token, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // doneToken 被取消（收到新邮件 / 单次 IDLE 到点），两种情况都继续下一轮
                }
                catch (ImapCommandException ex) when (ex.Response == ImapCommandResponse.Bad)
                {
                    // 少数服务器对 IDLE 返回 BAD：视同不支持，交给定时轮询
                    _logger.LogWarning(ex, "账户 {Account} 的服务器拒绝 IDLE，收信回退到定时轮询", account.Name);
                    return false;
                }
                finally
                {
                    inbox.CountChanged -= OnCountChanged;
                }

                if (Volatile.Read(ref changed) == 1)
                {
                    _logger.LogDebug("账户 {Account} 收件箱有新邮件（IDLE 通知）", account.Name);
                    await onInboxChanged(ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                await inbox.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "关闭 IDLE 收件箱失败");
            }
        }

        return true;
    }
}
