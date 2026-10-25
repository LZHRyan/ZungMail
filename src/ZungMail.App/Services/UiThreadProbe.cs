using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ZungMail.Infrastructure.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 界面线程占用探针：量「这段同步代码把 UI 线程占住了多久」，并把结果写进运行日志。
/// <para>
/// 用两类数据判定卡顿：<see cref="Measure"/> 包住的具名阶段（能定位到方法），
/// 以及 <see cref="Attach"/> 的心跳缺口（能兜住没包住的同步代码）。阈值遵循
/// <c>client-responsiveness</c> 规格：≥50ms 记 Info（便于对比基线），≥100ms 记 Warning（回归门禁）。
/// </para>
/// </summary>
public static class UiThreadProbe
{
    /// <summary>记录基线数字的门槛：低于这个量级的一次性开销没有分析价值。</summary>
    private const long InfoThresholdMs = 50;

    /// <summary>规格里的预算：超过即视为一次可见卡顿，必须留证。</summary>
    private const long WarnThresholdMs = 100;

    /// <summary>心跳间隔：DispatcherTimer 的正常抖动在 30ms 内，超过 100ms 的迟到必是同步占用。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(200);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static long _lastHeartbeatMs = Clock.ElapsedMilliseconds;

    private static DispatcherTimer? _heartbeat;

    /// <summary>包住一段「同步占着 UI 线程」的代码，释放时按耗时写日志。非 UI 线程调用直接空转。</summary>
    public static IDisposable Measure(string stage) => new StageScope(stage);

    /// <summary>启动心跳：没被 <see cref="Measure"/> 包住的同步占用，靠心跳迟到兜底发现。</summary>
    public static void Attach(Dispatcher dispatcher)
    {
        if (_heartbeat is not null) return;

        _lastHeartbeatMs = Clock.ElapsedMilliseconds;
        _heartbeat = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = HeartbeatInterval
        };
        _heartbeat.Tick += (_, _) => OnHeartbeat();
        _heartbeat.Start();
    }

    private static void OnHeartbeat()
    {
        var now = Clock.ElapsedMilliseconds;
        var gap = now - _lastHeartbeatMs - (long)HeartbeatInterval.TotalMilliseconds;
        _lastHeartbeatMs = now;

        if (gap < WarnThresholdMs) return;
        Log($"UI 线程占用 ~{gap}ms（未归因；检查是否有未包 Measure 的同步调用）", gap);
    }

    private static bool OnUiThread()
    {
        // 注意：本文件的命名空间是 ZungMail.App.Services，裸 Application 会解析到 ZungMail.Application 命名空间
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess();
    }

    private static void Log(string message, long elapsedMs)
    {
        try
        {
            if (elapsedMs >= WarnThresholdMs) AppLog.Warn(message);
            else AppLog.Info(message);
        }
        catch (Exception)
        {
            // 探针本身绝不能影响业务
        }
    }

    private sealed class StageScope : IDisposable
    {
        private readonly string _stage;
        private readonly long _startMs;
        private readonly bool _onUiThread;
        private bool _done;

        public StageScope(string stage)
        {
            _stage = stage;
            _onUiThread = OnUiThread();
            _startMs = _onUiThread ? Clock.ElapsedMilliseconds : 0;
        }

        public void Dispose()
        {
            if (_done || !_onUiThread) return;
            _done = true;

            var elapsed = Clock.ElapsedMilliseconds - _startMs;
            if (elapsed < InfoThresholdMs) return;

            Log($"UI 线程占用 {elapsed}ms（阶段：{_stage}）", elapsed);
        }
    }
}
