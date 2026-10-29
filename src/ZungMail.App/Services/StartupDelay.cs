namespace ZungMail.App.Services;

/// <summary>
/// 启动错峰：首屏时刻（账户 / 文件夹 / 列表三条查询）最怕被后台服务的写事务挡住。
/// 五个 <see cref="BackgroundService"/> 原来都是 <c>Task.Yield()</c> 后立刻开跑，
/// 10 万封库上一个清理轮次就可能让首屏干等好几秒；这里统一给一个可取消的初始延迟。
/// </summary>
internal static class StartupDelay
{
    /// <summary>等待一段初始延迟；被取消时直接返回（调用方随后会因 stoppingToken 退出循环）。</summary>
    public static async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;

        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 退出流程：不抛，交给调用方的循环条件收尾
        }
    }
}
