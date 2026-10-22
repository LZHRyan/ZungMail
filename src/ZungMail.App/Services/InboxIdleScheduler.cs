using System.Collections.Concurrent;
using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Mail;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// IMAP IDLE 推送收信：给每个启用的账户挂一条长连接监听收件箱，
/// 服务端一报告有新邮件就立刻做一次「仅收件箱」的增量同步，做到秒级可见。
/// <para>
/// 与 <see cref="InboxSyncScheduler"/> 并存：IDLE 只覆盖收件箱的新邮件，
/// 其它文件夹、标志位校准、以及服务器不支持 IDLE 的情况仍由定时轮询兜底。
/// </para>
/// </summary>
public sealed class InboxIdleScheduler : BackgroundService
{
    /// <summary>账户列表对账间隔（新增 / 停用账户最多这么久后生效）。</summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly IImapIdleWatcher _watcher;
    private readonly IAccountRepository _accounts;
    private readonly SynchronizationCoordinator _coordinator;
    private readonly MailReceiveNotifier _notifier;
    private readonly ILogger<InboxIdleScheduler> _logger;

    private readonly ConcurrentDictionary<long, CancellationTokenSource> _watchers = new();

    public InboxIdleScheduler(
        IImapIdleWatcher watcher,
        IAccountRepository accounts,
        SynchronizationCoordinator coordinator,
        MailReceiveNotifier notifier,
        ILogger<InboxIdleScheduler> logger)
    {
        _watcher = watcher;
        _accounts = accounts;
        _coordinator = coordinator;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartupTrace.Mark("定时器 InboxIdle 进入");

        using var timer = new PeriodicTimer(ReconcileInterval);

        try
        {
            await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            StartupTrace.Mark("定时器 InboxIdle 首轮完成");

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
        finally
        {
            foreach (var entry in _watchers)
            {
                entry.Value.Cancel();
                entry.Value.Dispose();
            }

            _watchers.Clear();
        }
    }

    /// <summary>让在跑监听器的账户集合与当前启用的账户保持一致。</summary>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        var accounts = (await _accounts.GetAllAsync(ct).ConfigureAwait(false))
            .Where(account => account.IsEnabled && account.HasCompleteConfig && !string.IsNullOrWhiteSpace(account.ImapHost))
            .ToList();

        foreach (var account in accounts)
        {
            if (_watchers.ContainsKey(account.Id)) continue;
            StartWatcher(account, ct);
        }

        foreach (var accountId in _watchers.Keys.ToList())
        {
            if (accounts.Any(account => account.Id == accountId)) continue;
            StopWatcher(accountId);
        }
    }

    private void StartWatcher(Account account, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_watchers.TryAdd(account.Id, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => SuperviseAsync(account, cts.Token), CancellationToken.None);
    }

    private void StopWatcher(long accountId)
    {
        if (!_watchers.TryRemove(accountId, out var cts)) return;
        cts.Cancel();
        cts.Dispose();
        _logger.LogInformation("账户 ID {AccountId} 的 IDLE 监听已停止", accountId);
    }

    /// <summary>守护一个账户的 IDLE 连接：掉线按退避重建，服务器不支持 IDLE 则彻底退出（交给轮询）。</summary>
    private async Task SuperviseAsync(Account account, CancellationToken ct)
    {
        var backoff = MinBackoff;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var supported = await _watcher
                    .WatchInboxAsync(account, token => OnInboxChangedAsync(account, token), ct)
                    .ConfigureAwait(false);

                if (!supported) return; // 服务器不支持 IDLE：定时轮询已经够用，不再重试
                backoff = MinBackoff;   // 连接正常结束（多半是我们主动断开），重置退避
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "账户 {Account} 的 IDLE 监听中断，{Seconds:F1}s 后重连", account.Name, backoff.TotalSeconds);
            }

            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>IDLE 报来新邮件：立刻做一次仅收件箱的增量同步，并把结果推给界面 / 托盘。</summary>
    private async Task OnInboxChangedAsync(Account account, CancellationToken ct)
    {
        try
        {
            var outcome = await _coordinator.SyncInboxWithOutcomeAsync(account.Id, ct).ConfigureAwait(false);
            if (outcome is null) return; // 门被其它同步占着：那一轮会把这封邮件收进来

            _notifier.Publish(outcome);
            _notifier.PublishCompleted(new[] { outcome });

            if (outcome.NewMessages > 0)
            {
                _logger.LogInformation(
                    "IDLE 即时收信：账户 {Account} 新增 {Count} 封，耗时 {Elapsed:F1}s",
                    account.Name, outcome.NewMessages, outcome.Duration.TotalSeconds);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "账户 {Account} 的 IDLE 即时同步失败，等待下一轮轮询补上", account.Name);
        }
    }
}
