using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZungMail.App.Views;

/// <summary>
/// 无边框窗口（WindowStyle=None + WindowChrome）最大化时，按「显示器工作区」而不是整屏计算尺寸。
/// 不处理的话窗口会比工作区高出调整边框那一段，底部状态栏/按钮会被任务栏裁掉。
/// 主窗口与所有自绘对话框共用这一份实现。
/// </summary>
internal static class WindowMaximizeBounds
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT Reserved;
        public POINT MaxSize;
        public POINT MaxPosition;
        public POINT MinTrackSize;
        public POINT MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>挂上 WM_GETMINMAXINFO 钩子；窗口已创建时立即挂，否则等 SourceInitialized。</summary>
    public static void Attach(Window window)
    {
        if (window is null) return;

        void HookNow()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            HwndSource.FromHwnd(handle)?.AddHook(Hook);
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            HookNow();
            return;
        }

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            HookNow();
        }

        window.SourceInitialized += OnSourceInitialized;
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo || hwnd == IntPtr.Zero || lParam == IntPtr.Zero) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.MaxPosition = new POINT
        {
            X = info.Work.Left - info.Monitor.Left,
            Y = info.Work.Top - info.Monitor.Top
        };
        mmi.MaxSize = new POINT
        {
            X = info.Work.Right - info.Work.Left,
            Y = info.Work.Bottom - info.Work.Top
        };
        mmi.MaxTrackSize = mmi.MaxSize;

        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }
}
