using ZungMail.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 待发送队列后台调度器（FR-08）：程序只要在运行，就周期扫描到点邮件并投递，
/// 不再依赖用户手动打开发件箱点「立即投递」，使定时发送真正闭环。
/// </summary>
public sealed class OutboxScheduler : BackgroundService
{
    /// <summary>扫描间隔：定时发送精度按分钟计，20 秒足以保证准点投递。</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(20);

    private readonly IOutboxProcessor _processor;
    private readonly ILogger<OutboxScheduler> _logger;

    public OutboxScheduler(IOutboxProcessor processor, ILogger<OutboxScheduler> logger)
    {
        _processor = processor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartupTrace.Mark("定时器 Outbox 进入");

        // 首轮里的 DB 调用是「假异步」（同步完成），会占住调用 Host.StartAsync 的线程；先让出调度器
        await Task.Yield();

        // 先回收上次异常退出残留的「发送中」：服务器可能已接受，结果未知，不能自动重发。
        try
        {
            var recovered = await _processor.RecoverInterruptedAsync(stoppingToken).ConfigureAwait(false);
            if (recovered > 0)
            {
                _logger.LogWarning("{Count} 封邮件的投递被中断，已标记为「发送结果未知」，请在发件箱中确认", recovered);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "回收中断的投递失败");
        }

        using var timer = new PeriodicTimer(ScanInterval);

        try
        {
            // 先投递一次，补上程序未运行期间已到点的邮件
            await ScanAsync(stoppingToken).ConfigureAwait(false);
            StartupTrace.Mark("定时器 Outbox 首轮完成");

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await ScanAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 程序退出，正常结束调度
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        try
        {
            var sent = await _processor.ProcessDueAsync(ct).ConfigureAwait(false);
            if (sent > 0) _logger.LogInformation("后台调度已投递 {Count} 封待发送邮件", sent);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单轮扫描失败不终止调度，等待下一轮重试
            _logger.LogError(ex, "后台投递扫描失败");
        }
    }
}
