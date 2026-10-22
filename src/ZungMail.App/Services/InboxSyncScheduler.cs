using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 按用户设置的间隔执行增量收信。
/// <para>
/// 用 <see cref="PeriodicTimer"/> 固定节拍，而不是「跑完一轮再等 N 分钟」：
/// 后者在文件夹多、服务器慢时会把实际周期拖成「轮次耗时 + N 分钟」，
/// 用户设 1 分钟却十分钟才收到信。固定节拍下，一轮超时只会顺延，
/// 不会把下一轮往后推一整个间隔。
/// </para>
/// 单轮异常只写入日志，下一轮仍可继续；应用启动后的首次同步仍由主窗口负责，以便展示实时进度。
/// </summary>
public sealed class InboxSyncScheduler : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);

    private readonly SynchronizationCoordinator _syncCoordinator;
    private readonly ISettingsStore _settings;
    private readonly MailReceiveNotifier _notifier;
    private readonly ILogger<InboxSyncScheduler> _logger;

    public InboxSyncScheduler(
        SynchronizationCoordinator syncCoordinator,
        ISettingsStore settings,
        MailReceiveNotifier notifier,
        ILogger<InboxSyncScheduler> logger)
    {
        _syncCoordinator = syncCoordinator;
        _settings = settings;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>用户设置的有效同步间隔：夹取到 1–120 分钟。</summary>
    public static TimeSpan EffectiveInterval(int minutes)
        => TimeSpan.FromMinutes(AppSettings.EffectiveSyncIntervalMinutes(minutes));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartupTrace.Mark("定时器 InboxSync 进入");

        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);

            var interval = EffectiveInterval(_settings.Current.SyncIntervalMinutes);
            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SyncOnceAsync(stoppingToken).ConfigureAwait(false);

                // 用户中途改了间隔：换一只节拍器，下一拍按新间隔算
                var latest = EffectiveInterval(_settings.Current.SyncIntervalMinutes);
                if (latest != timer.Period)
                {
                    timer.Period = latest;
                    _logger.LogInformation("同步间隔已调整为 {Minutes} 分钟", latest.TotalMinutes);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        try
        {
            var started = DateTimeOffset.UtcNow;
            var outcomes = await _syncCoordinator.SyncIncrementAsync(ct).ConfigureAwait(false);

            foreach (var outcome in outcomes) _notifier.Publish(outcome);

            // 界面订阅者据此刷新列表与侧栏未读数（后台同步不再「只改状态栏」）
            _notifier.PublishCompleted(outcomes);

            var newMessages = outcomes.Sum(outcome => outcome.NewMessages);
            _logger.LogDebug(
                "后台增量同步完成：{Accounts} 个账户，新增 {New} 封，耗时 {Elapsed:F1}s",
                outcomes.Count, newMessages, (DateTimeOffset.UtcNow - started).TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "后台收信失败，下一轮将自动重试");
        }
    }
}
