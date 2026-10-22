using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace ZungMail.App.Views;

/// <summary>
/// 无边框对话框外壳：统一窗口装饰、居中于主窗口、标题栏可拖动。
/// 撰写 / 账户配置 / 设置 / 发件箱等窗口均继承此类。
/// </summary>
public abstract class DialogWindow : Window
{
    protected DialogWindow()
    {
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),

            // 保留底边 1px 玻璃边框：无边框窗口只有带非客户区时 DWM 才绘制系统投影。
            // 四边都留会在正文贴边处出现 1px 半透明细缝，所以只开底边。
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });

        // 自绘标题栏按钮依赖系统菜单命令，Window 不会自动注册，必须显式绑定
        WindowChromeCommands.Attach(this);

        // 无边框窗口最大化时按工作区计算尺寸，避免底部被任务栏裁掉
        WindowMaximizeBounds.Attach(this);
    }

    /// <summary>标题栏拖动 / 双击最大化。</summary>
    protected void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { /* 非交互状态下的拖动请求可忽略 */ }
    }
}
