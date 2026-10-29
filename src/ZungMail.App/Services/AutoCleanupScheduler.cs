using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 自动定期清理调度器（需求 3）：程序运行期间按设置的间隔清理超过保留期的
/// 垃圾箱 / 广告邮件，并顺带彻底删除超过隔离期的数据。所有清理同样写入审计表，
/// 隔离期内可撤销，收件箱 / 已发送 / 归档 / 星标 / 草稿均不在自动清理范围内。
/// </summary>
public sealed class AutoCleanupScheduler : BackgroundService
{
    /// <summary>检查间隔：自动清理按小时计，10 分钟检查一次足够。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(10);

    private readonly ICleanupService _cleanup;
    private readonly ISettingsStore _settings;
    private readonly IAccountRepository _accounts;
    private readonly ILogger<AutoCleanupScheduler> _logger;

    public AutoCleanupScheduler(
        ICleanupService cleanup,
        ISettingsStore settings,
        IAccountRepository accounts,
        ILogger<AutoCleanupScheduler> logger)
    {
        _cleanup = cleanup;
        _settings = settings;
        _accounts = accounts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartupTrace.Mark("定时器 AutoCleanup 进入");

        // 首轮里的 DB 调用是「假异步」（同步完成），会占住调用 Host.StartAsync 的线程；先让出调度器
        await Task.Yield();

        // 启动错峰：别和首屏那三条查询抢库（写事务会挡住列表读）
        await StartupDelay.WaitAsync(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TickInterval);

        try
        {
            // 启动时先跑一轮，补上程序未运行期间到期的任务
            await RunAsync(stoppingToken).ConfigureAwait(false);
            StartupTrace.Mark("定时器 AutoCleanup 首轮完成");

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 程序退出，正常结束调度
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // 超期隔离数据无条件清理，避免撤销窗口无限增长
            var freed = await _cleanup.PurgeExpiredAsync(ct).ConfigureAwait(false);
            if (freed > 0) _logger.LogInformation("自动清理已释放 {Bytes} 字节隔离数据", freed);

            var settings = _settings.Current;
            if (!settings.AutoCleanupEnabled) return;
            if (!settings.AutoCleanupIncludeTrash && !settings.AutoCleanupIncludeJunk) return;

            var interval = TimeSpan.FromHours(Math.Max(1, settings.AutoCleanupIntervalHours));
            var jobs = await _cleanup.GetJobsAsync(100, ct).ConfigureAwait(false);
            var last = jobs
                .Where(j => j.Mode == CleanupMode.Auto)
                .MaxBy(j => j.StartedUtc);

            if (last is not null && DateTimeOffset.UtcNow - last.StartedUtc < interval) return;

            var filter = new CleanupFilter
            {
                IncludeTrash = settings.AutoCleanupIncludeTrash,
                IncludeJunk = settings.AutoCleanupIncludeJunk,
                IncludeInbox = false,
                IncludeSent = false,
                IncludeArchive = false,
                BeforeUtc = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, settings.AutoCleanupRetentionDays)),
                ProtectStarred = true,
                // 垃圾箱 / 广告中的未读邮件同样属于待清理对象
                ProtectUnread = false,
                // 停用账户只暂停收发，本地邮件仍需可读，不参与自动清理
                ExcludeAccountIds = (await _accounts.GetAllAsync(ct).ConfigureAwait(false))
                    .Where(account => !account.IsEnabled)
                    .Select(account => account.Id)
                    .ToList()
            };

            var job = await _cleanup.ExecuteAsync(filter, null, CleanupMode.Auto, null, ct).ConfigureAwait(false);
            if (job is not null)
            {
                _logger.LogInformation(
                    "自动清理完成：任务 #{JobId}，{Processed}/{Total} 封，待释放 {Bytes} 字节",
                    job.Id, job.ProcessedCount, job.TotalCount, job.TotalBytes);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单轮失败不终止调度，等待下一轮重试
            _logger.LogError(ex, "自动清理调度失败");
        }
    }
}
