using System;
using System.Threading.Tasks;
using ZungMail.Infrastructure.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 火忘式异步调用的统一入口：不让异常悄悄消失，也不让后台续体跑到错误的线程上。
/// <para>
/// 视图模型里大量「属性变了顺便刷一下」的调用（刷新列表、补徽标、加载详情）不适合 await，
/// 但裸写 <c>_ = XxxAsync()</c> 时异常只会打到全局处理器、连调用点都定位不到。
/// 这里统一记日志，并保持回到原同步上下文（UI 线程）执行后续代码。
/// </para>
/// </summary>
public static class FireAndForget
{
    /// <summary>启动一个火忘式任务：正常结束静默，取消静默，异常记 error 日志。</summary>
    public static void Run(Func<Task> action, string name, Action<Exception>? onError = null)
    {
        _ = RunCore(action, name, onError);
    }

    private static async Task RunCore(Func<Task> action, string name, Action<Exception>? onError)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 用户操作被新操作取代，属正常路径
        }
        catch (Exception ex)
        {
            AppLog.Error($"后台任务失败：{name}", ex);
            onError?.Invoke(ex);
        }
    }
}
