using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 全局热键（Foxmail 的「显示/隐藏主界面」热键）：RegisterHotKey + 窗口 Hook 接收 WM_HOTKEY。
/// 注册失败（例如组合已被其它程序占用）时返回可读原因，并保持未注册状态。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x4D43;       // 'MC'
    private const int WmHotkey = 0x0312;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly ILogger<GlobalHotkeyService> _logger;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;

    public GlobalHotkeyService(ILogger<GlobalHotkeyService> logger) => _logger = logger;

    /// <summary>热键被按下（已在界面线程）。</summary>
    public event EventHandler? Pressed;

    public bool IsRegistered => _registered;

    /// <summary>当前已注册的热键显示文本；未注册为空。</summary>
    public string CurrentDisplay { get; private set; } = "";

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>绑定主窗口的窗口句柄与消息 Hook；重复调用安全。</summary>
    public bool Attach(Window window)
    {
        if (_source is not null) return true;

        var helper = new WindowInteropHelper(window);
        _handle = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        if (_source is null)
        {
            _logger.LogWarning("无法取得窗口消息源，全局热键不可用");
            return false;
        }

        _source.AddHook(WndProc);
        return true;
    }

    /// <summary>按设置应用热键：关闭时注销，开启且能解析时尝试注册。</summary>
    public (bool Ok, string Message) Apply(bool enabled, string? gestureText)
    {
        Unregister();

        if (!enabled)
        {
            return (true, "已关闭全局热键");
        }

        if (!HotkeyGesture.TryParse(gestureText, out var gesture) || gesture is null)
        {
            return (false, "热键格式无效，示例：Ctrl+Alt+M");
        }

        if (_source is null)
        {
            return (false, "主窗口尚未就绪，无法注册全局热键");
        }

        var modifiers = ToWin32Modifiers(gesture.Modifiers) | ModNoRepeat;
        if (!TryGetVirtualKey(gesture.Key, out var vk))
        {
            return (false, "不支持的按键：" + gesture.Key);
        }

        if (!RegisterHotKey(_handle, HotkeyId, modifiers, vk))
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning("注册全局热键 {Gesture} 失败，Win32 错误码 {Code}", gesture.Display, error);
            return (false, $"注册失败，快捷键可能已被其它程序占用（错误码 {error}）");
        }

        _registered = true;
        CurrentDisplay = gesture.Display;
        return (true, $"已启用全局热键：{gesture.Display}");
    }

    /// <summary>注销当前热键（未注册时忽略）。</summary>
    public void Unregister()
    {
        if (!_registered) return;

        try
        {
            UnregisterHotKey(_handle, HotkeyId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "注销全局热键失败");
        }
        finally
        {
            _registered = false;
            CurrentDisplay = "";
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try
            {
                Pressed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理全局热键回调失败");
            }
        }

        return IntPtr.Zero;
    }

    private static uint ToWin32Modifiers(HotkeyModifiers modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) value |= ModAlt;
        if (modifiers.HasFlag(HotkeyModifiers.Control)) value |= ModControl;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) value |= ModShift;
        if (modifiers.HasFlag(HotkeyModifiers.Win)) value |= ModWin;
        return value;
    }

    private static bool TryGetVirtualKey(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1)
        {
            var c = key[0];
            vk = c is >= 'A' and <= 'Z' or >= '0' and <= '9' ? (uint)c : 0u;
            return vk != 0;
        }

        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out var number)
            && number is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + number - 1);   // VK_F1 = 0x70
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = IntPtr.Zero;
    }
}
