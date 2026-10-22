using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 运行日志保留期调度：<c>logs\app-日期.log</c> 只保留最近 N 天（最多 7 天，默认 7 天），超期自动删除。
/// <para>
/// 每小时检查一次即可——日志按天滚动，清理不需要更实时；启动 10 秒后先跑一轮，
/// 把程序没运行期间到期的文件补删掉。设置里改了保留天数，下一轮就按新值执行，无需重启。
/// </para>
/// </summary>
public sealed class LogRetentionScheduler : BackgroundService
{
    /// <summary>检查间隔：日志按天滚动，1 小时足够，代价只是读一次目录。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// 首轮延迟：启动瞬间不在关键路径上做文件操作（也避开启动阶段多路日志同时首写的窗口）。
    /// 日志按天滚动，晚 10 秒清理没有任何影响。
    /// </summary>
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(10);

    private readonly ISettingsStore _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger<LogRetentionScheduler> _logger;

    public LogRetentionScheduler(
        ISettingsStore settings,
        IAppPaths paths,
        ILogger<LogRetentionScheduler> logger)
    {
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartupTrace.Mark("定时器 日志清理 进入");

        using var timer = new PeriodicTimer(TickInterval);

        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken).ConfigureAwait(false);
            RunOnce();
            StartupTrace.Mark("定时器 日志清理 首轮完成");

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                RunOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // 程序退出，正常结束调度
        }
    }

    private void RunOnce()
    {
        try
        {
            var keepDays = AppSettings.EffectiveLogRetentionDays(_settings.Current.LogRetentionDays);
            var result = LogRetention.Run(
                _paths.LogDirectory,
                keepDays,
                DateTime.Now,
                (path, ex) => _logger.LogWarning(ex, "删除超期日志失败（下一轮重试）：{Path}", path));

            if (result.AnyDeleted)
            {
                _logger.LogInformation(
                    "日志清理完成：删除 {Files} 个超期文件，释放 {Bytes} 字节（保留 {Days} 天）",
                    result.DeletedFiles, result.DeletedBytes, keepDays);
            }

            if (result.FailedFiles > 0)
            {
                _logger.LogWarning("日志清理：{Count} 个超期文件本轮未能删除", result.FailedFiles);
            }
        }
        catch (Exception ex)
        {
            // 清理失败绝不影响主流程，等下一轮
            _logger.LogError(ex, "日志清理失败");
        }
    }
}
