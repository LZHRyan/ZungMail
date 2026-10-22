using System.Runtime.InteropServices;

namespace ZungMail.App.Services;

/// <summary>系统级空闲时长（距最后一次键盘/鼠标输入），用于客户端自动锁定。</summary>
public static class IdleTime
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>当前空闲时长；查询失败时返回 <see cref="TimeSpan.Zero"/>（不触发自动锁定）。</summary>
    public static TimeSpan GetIdleTime()
    {
        var info = new LastInputInfo { CbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

        // dwTime 为 32 位 tick，无符号差值可自动跨越回绕。
        var idleMs = unchecked((uint)Environment.TickCount - info.DwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
