using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 托盘常驻与桌面通知：关闭主窗口时隐藏到托盘，保证定时发送在后台继续生效；
/// 投递成功 / 失败以托盘气泡反馈（设置项「新邮件桌面通知」关闭时静默）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IOutboxProcessor _processor;
    private readonly MailReceiveNotifier _receiveNotifier;
    private readonly UpdateCoordinator _updates;
    private readonly UpdateWindowService _updateWindows;
    private readonly ILogger<TrayIconService> _logger;

    private NotifyIcon? _icon;
    private System.Windows.Window? _window;
    private bool _exiting;
    private string? _notifiedUpdateVersion;

    /// <summary>从 EXE 提取的托盘图标（HICON）：NotifyIcon 不会替调用方释放它。</summary>
    private Icon? _ownedIcon;

    /// <summary>菜单项自建的加粗字体：ToolStripMenuItem 也不负责释放。</summary>
    private readonly System.Collections.Generic.List<Font> _ownedFonts = new();

    public TrayIconService(
        ISettingsStore settings,
        IOutboxProcessor processor,
        MailReceiveNotifier receiveNotifier,
        UpdateCoordinator updates,
        UpdateWindowService updateWindows,
        ILogger<TrayIconService> logger)
    {
        _settings = settings;
        _processor = processor;
        _receiveNotifier = receiveNotifier;
        _updates = updates;
        _updateWindows = updateWindows;
        _logger = logger;
    }

    /// <summary>创建托盘图标，并接管主窗口的关闭行为。</summary>
    public void Attach(System.Windows.Window window)
    {
        _window = window;
        window.Closing += OnWindowClosing;

        _processor.ItemSent += OnItemSent;
        _processor.ItemFailed += OnItemFailed;
        _receiveNotifier.NewMailReceived += OnNewMailReceived;
        _updates.StateChanged += OnUpdateStateChanged;

        _icon = new NotifyIcon
        {
            // 与 EXE / 任务栏使用同一嵌入式 Logo，路径不可用时保留系统图标作为安全回退。
            // 提取出来的 HICON 由本类持有并在 Dispose 时释放；SystemIcons.Application 是共享静态对象，不能释放。
            Icon = ResolveTrayIcon(),
            Text = "ZungMail",
            // 交给系统按托盘图标位置弹出：多显示器 / 高 DPI 下位置最稳
            ContextMenuStrip = BuildMenu(),
            Visible = true
        };
        _icon.DoubleClick += (_, _) => RestoreWindow();
        _icon.BalloonTipClicked += (_, _) => RestoreWindow();
    }

    private Icon ResolveTrayIcon()
    {
        if (System.Environment.ProcessPath is { } executablePath &&
            Icon.ExtractAssociatedIcon(executablePath) is { } extracted)
        {
            _ownedIcon = extracted;
            return extracted;
        }

        return SystemIcons.Application;
    }

    /// <summary>
    /// 托盘菜单：圆角卡片 + 主题配色 + 矢量图标，和主界面右键菜单同一套观感。
    /// 每次弹出前按当前主题刷新一次，深浅色切换后不用重启。
    /// </summary>
    private TrayMenuStrip BuildMenu()
    {
        var iconPixels = TrayMenuIcons.PixelsFor(TrayMenuIcons.SystemDpiScale());
        var menu = new TrayMenuStrip(TrayMenuColors.FromTheme())
        {
            ShowImageMargin = true,
            ShowCheckMargin = false,
            DropShadowEnabled = false,
            IconPixels = iconPixels
        };
        menu.ImageScalingSize = new Size(iconPixels, iconPixels);

        menu.Items.Add(CreateItem(menu, "IcoMail", "InkBrush", "打开 ZungMail", RestoreWindow, primary: true));
        menu.Items.Add(CreateItem(menu, "IcoSend", "Ink2Brush", "立即投递到期邮件", () => _ = SendDueAsync()));
        menu.Items.Add(CreateItem(menu, "IcoRefresh", "AccentBrush", "检查更新", () => _updateWindows.Show()));
        menu.Items.Add(new ToolStripSeparator { Margin = new Padding(0, 4, 0, 4) });
        menu.Items.Add(CreateItem(menu, "IcoPower", "ErrorDotBrush", "退出 ZungMail", ExitApplication));

        menu.Opening += (_, _) => menu.RefreshTheme(TrayMenuColors.FromTheme());
        return menu;
    }

    /// <summary>菜单项：矢量图标 + 文本，行高与主界面右键菜单一致（上下 6px 内边距）。</summary>
    private ToolStripMenuItem CreateItem(
        TrayMenuStrip menu, string iconKey, string brushKey, string text, Action action, bool primary = false)
    {
        var item = new ToolStripMenuItem(text)
        {
            Image = TrayMenuIcons.Create(iconKey, brushKey, menu.IconPixels),
            ImageScaling = ToolStripItemImageScaling.None,
            Padding = new Padding(0, 6, 0, 6),
            ForeColor = menu.Colors.Text,
            // 主题刷新时据此重建图标
            Tag = new[] { iconKey, brushKey }
        };
        if (primary)
        {
            // new Font(…) 是在原字体上克隆出的新句柄，菜单项销毁时不会自动释放，统一记下来
            var bold = new Font(item.Font, FontStyle.Bold);
            item.Font = bold;
            _ownedFonts.Add(bold);
        }

        item.Click += (_, _) => action();
        return item;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || !_settings.Current.CloseToTray) return;

        // 隐藏而非退出，否则程序结束后定时邮件将无人投递
        e.Cancel = true;
        _window?.Hide();

        _icon?.ShowBalloonTip(
            3000,
            "ZungMail 仍在运行",
            "已最小化到托盘，定时邮件会继续发送。右键托盘图标可退出。",
            ToolTipIcon.Info);
    }

    private void OnItemSent(object? sender, OutboxItem item)
    {
        if (!_settings.Current.DesktopNotify) return;

        Dispatch(() => _icon?.ShowBalloonTip(
            4000,
            "邮件已发送",
            $"{item.SubjectDisplay}\n{item.RecipientsDisplay}",
            ToolTipIcon.Info));
    }

    private void OnNewMailReceived(object? sender, NewMailNotification notification)
    {
        if (!_settings.Current.DesktopNotify) return;

        Dispatch(() => _icon?.ShowBalloonTip(
            5000,
            "收到新邮件",
            notification.Text,
            ToolTipIcon.Info));
    }

    /// <summary>自动检测到新版本时用托盘气泡提醒一次；同一版本不重复打扰。</summary>
    private void OnUpdateStateChanged(object? sender, UpdateStateChangedEventArgs e)
    {
        if (e.Phase != UpdatePhase.UpdateAvailable || _updates.Release is not { } release) return;
        if (string.Equals(_notifiedUpdateVersion, release.Version, StringComparison.Ordinal)) return;
        if (!_settings.Current.DesktopNotify) return;

        _notifiedUpdateVersion = release.Version;
        var title = release.Mandatory ? $"必须升级到 v{release.Version}" : $"发现新版本 v{release.Version}";
        Notify(title + "（点击托盘图标查看）", "打开 ZungMail 后可在状态栏或设置 → 升级 中下载安装。");
    }

    private void OnItemFailed(object? sender, OutboxItem item)
    {
        if (!_settings.Current.DesktopNotify) return;

        Dispatch(() => _icon?.ShowBalloonTip(
            5000,
            $"邮件发送失败（{item.AttemptsDisplay}）",
            $"{item.SubjectDisplay}\n{item.LastError}",
            ToolTipIcon.Warning));
    }

    /// <summary>供后台服务调用的通用气泡通知（如待办到期提醒）。</summary>
    public void Notify(string title, string message, bool warning = false)
    {
        Dispatch(() => _icon?.ShowBalloonTip(
            6000,
            title,
            message,
            warning ? ToolTipIcon.Warning : ToolTipIcon.Info));
    }

    private async Task SendDueAsync()
    {
        try
        {
            await _processor.ProcessDueAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从托盘触发投递失败");
        }
    }

    private void RestoreWindow() => Dispatch(() =>
    {
        if (_window is null) return;

        _window.Show();
        if (_window.WindowState == System.Windows.WindowState.Minimized)
        {
            _window.WindowState = System.Windows.WindowState.Normal;
        }

        _window.Activate();
    });

    private void ExitApplication() => Dispatch(() =>
    {
        _exiting = true;
        System.Windows.Application.Current?.Shutdown();
    });

    /// <summary>托盘事件可能在后台线程触发，统一切回界面线程访问窗体与图标。</summary>
    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _processor.ItemSent -= OnItemSent;
        _processor.ItemFailed -= OnItemFailed;
        _receiveNotifier.NewMailReceived -= OnNewMailReceived;
        _updates.StateChanged -= OnUpdateStateChanged;

        if (_window is not null) _window.Closing -= OnWindowClosing;
        _window = null;

        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }

        foreach (var font in _ownedFonts) font.Dispose();
        _ownedFonts.Clear();

        _ownedIcon?.Dispose();
        _ownedIcon = null;
    }
}
