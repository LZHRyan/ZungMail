using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 等待回复的巡检：每 5 分钟检查一次到点的跟进。
/// <list type="bullet">
/// <item>条件已满足（对方回复了）→ 自动结束，不打扰。</item>
/// <item>仍未回复 → 托盘提醒一次，并停在本轮，由用户决定「继续跟进」或「催信」。</item>
/// </list>
/// 提醒复用托盘通道，与待办到期提醒一致。设置里关掉货代「等待回复」后不再巡检。
/// </summary>
public sealed class FollowUpScheduler : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    private readonly MailFollowUpService _followUps;
    private readonly TrayIconService _tray;
    private readonly ISettingsStore _settings;
    private readonly ILogger<FollowUpScheduler> _logger;

    public FollowUpScheduler(
        MailFollowUpService followUps,
        TrayIconService tray,
        ISettingsStore settings,
        ILogger<FollowUpScheduler> logger)
    {
        _followUps = followUps;
        _tray = tray;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartupTrace.Mark("定时器 FollowUp 进入");

        // 首轮里的 DB 调用是「假异步」（同步完成），会占住调用 Host.StartAsync 的线程；先让出调度器
        await Task.Yield();

        // 启动错峰：别和首屏那三条查询抢库（写事务会挡住列表读）
        await StartupDelay.WaitAsync(TimeSpan.FromSeconds(25), stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TickInterval);

        try
        {
            // 启动后先补一次：关闭客户端期间到点的跟进也要提醒
            await CheckAsync(stoppingToken).ConfigureAwait(false);
            StartupTrace.Mark("定时器 FollowUp 首轮完成");

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        // 设置里关掉「等待回复」后不再巡检：避免继续推托盘提醒
        if (!FreightFeatureFlags.From(_settings.Current).FollowUps) return;

        try
        {
            var due = await _followUps.CheckDueAsync(DateTimeOffset.Now, ct).ConfigureAwait(false);
            if (due.Count == 0) return;

            var first = due[0];
            var extra = due.Count > 1 ? $"\n另有 {due.Count - 1} 封邮件等待回复" : "";
            _tray.Notify(
                "等待回复",
                $"{first.FollowUp.SubjectDisplay}\n{first.PendingText}{extra}",
                warning: true);

            _logger.LogInformation("已提醒 {Count} 条等待回复", due.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单轮失败不影响下一轮
            _logger.LogWarning(ex, "等待回复巡检失败");
        }
    }
}
