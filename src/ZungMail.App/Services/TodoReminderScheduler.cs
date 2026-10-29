using ZungMail.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 待办到期提醒：每 5 分钟扫描一次已到期且未提醒过的待办，通过托盘气泡提示。
/// 提醒后写入 reminded_utc，避免同一事项反复打扰；截止时间被修改时会重置该标记。
/// </summary>
public sealed class TodoReminderScheduler : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    private readonly MailTodoService _todos;
    private readonly TrayIconService _tray;
    private readonly ILogger<TodoReminderScheduler> _logger;

    public TodoReminderScheduler(
        MailTodoService todos,
        TrayIconService tray,
        ILogger<TodoReminderScheduler> logger)
    {
        _todos = todos;
        _tray = tray;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartupTrace.Mark("定时器 TodoReminder 进入");

        // 首轮里的 DB 调用是「假异步」（同步完成），会占住调用 Host.StartAsync 的线程；先让出调度器
        await Task.Yield();

        // 启动错峰：别和首屏那三条查询抢库（写事务会挡住列表读）
        await StartupDelay.WaitAsync(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TickInterval);

        try
        {
            // 启动后先补一次：关闭客户端期间到期的待办也能被提醒
            await RemindAsync(stoppingToken).ConfigureAwait(false);
            StartupTrace.Mark("定时器 TodoReminder 首轮完成");

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RemindAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    private async Task RemindAsync(CancellationToken ct)
    {
        try
        {
            var due = await _todos.GetDueForReminderAsync(DateTimeOffset.Now, 10, ct).ConfigureAwait(false);
            if (due.Count == 0) return;

            var first = due[0];
            var extra = due.Count > 1 ? $"\n另有 {due.Count - 1} 项待办已到期" : "";
            _tray.Notify("待办已到期", $"{first.Title}\n{first.DueDisplay}{extra}", warning: true);

            foreach (var todo in due)
            {
                await _todos.MarkRemindedAsync(todo.Id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            }

            _logger.LogInformation("已提醒 {Count} 项到期待办", due.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单轮失败不影响下一轮
            _logger.LogWarning(ex, "待办到期提醒失败");
        }
    }
}
