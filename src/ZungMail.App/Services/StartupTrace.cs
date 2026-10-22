using System.Diagnostics;
using ZungMail.Infrastructure.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 启动分段计时：从进程创建到主界面可交互，每跨过一个阶段就立刻落一行日志。
/// <para>
/// 只在真实进程启动时启用（<see cref="Enable"/>）；测试里既不写日志也不建目录。
/// 之所以「每段一行」而不是最后汇总一行：启动卡住时最后一条日志就是卡住的位置，
/// 不需要等到界面出来才能判断（老版本只有汇总行，卡住时日志里什么都看不到）。
/// </para>
/// </summary>
public static class StartupTrace
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static bool _enabled;

    /// <summary>是否处于启动跟踪状态。</summary>
    public static bool Enabled => _enabled;

    /// <summary>自跟踪开始（程序入口）以来的毫秒数。</summary>
    public static long ElapsedMs => Clock.ElapsedMilliseconds;

    /// <summary>
    /// 进程创建 → 程序入口的耗时：包含 hostfxr 启动、CLR 初始化、
    /// WPF / 依赖程序集装载与首次 JIT，是「双击图标后一段时间的空白」最直接的度量。
    /// 取不到（权限受限）时为 -1。
    /// </summary>
    public static long ProcessStartToEntryMs { get; private set; } = -1;

    /// <summary>开启跟踪并记录入口时间；重复调用无副作用。</summary>
    public static void Enable()
    {
        if (_enabled) return;
        _enabled = true;

        try
        {
            using var process = Process.GetCurrentProcess();
            ProcessStartToEntryMs = (long)(DateTime.Now - process.StartTime).TotalMilliseconds;
        }
        catch (Exception)
        {
            // 受权限限制取不到进程启动时间：保持 -1，不影响启动
        }

        Mark("进程入口");
    }

    /// <summary>记录一个启动阶段（带自入口的累计毫秒数）。</summary>
    public static void Mark(string stage)
    {
        if (!_enabled) return;

        var elapsed = Clock.ElapsedMilliseconds;
        try
        {
            lock (Gate)
            {
                AppLog.Info($"启动阶段：{stage} · 自入口 {elapsed}ms");
            }
        }
        catch (Exception)
        {
            // 计时埋点绝不能反过来影响启动
        }
    }
}
