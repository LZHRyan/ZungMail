using System.Windows;
using System.Windows.Input;

namespace ZungMail.App.Views;

/// <summary>
/// 无边框窗口的系统菜单命令绑定。
///
/// <see cref="Window"/> 本身不会为 <see cref="SystemCommands"/> 注册命令绑定，
/// 而窗口使用 <see cref="System.Windows.Shell.WindowChrome"/> 且 <c>WindowStyle="None"</c> 时
/// 也没有系统标题栏接管这些命令。缺少绑定时最小化 / 最大化 / 关闭按钮的 <c>CanExecute</c>
/// 恒为 <c>false</c>：按钮被禁用，点击毫无反应。这里为每个自绘标题栏窗口补上绑定。
/// </summary>
internal static class WindowChromeCommands
{
    /// <summary>为指定窗口注册最小化 / 最大化 / 关闭命令。</summary>
    public static void Attach(Window window)
    {
        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.MinimizeWindowCommand,
            (_, _) => window.WindowState = WindowState.Minimized,
            (_, e) => e.CanExecute = true));

        // 同一个按钮承担「最大化 / 还原」，点击时按当前状态取反。
        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.MaximizeWindowCommand,
            (_, _) => window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized,
            (_, e) => e.CanExecute = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip));

        window.CommandBindings.Add(new CommandBinding(
            SystemCommands.CloseWindowCommand,
            (_, _) => window.Close(),
            (_, e) => e.CanExecute = true));
    }
}
