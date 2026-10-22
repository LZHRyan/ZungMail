using System.Windows;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Services;

/// <summary>
/// 独立阅读窗口的单实例宿主：整个客户端只开一个阅读窗口，
/// 双击另一封邮件时复用同一窗口切换内容，避免窗口与 WebView2 实例无限增长。
/// </summary>
public sealed class MailReaderWindowService
{
    private readonly MailReaderViewModel _viewModel;
    private Views.MailReaderWindow? _window;

    public MailReaderWindowService(MailReaderViewModel viewModel) => _viewModel = viewModel;

    /// <summary>在独立阅读窗口打开指定邮件；窗口已存在时复用并前置。</summary>
    public void Show(long emailId)
    {
        var window = EnsureWindow();

        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        window.Activate();

        // 先让窗口上屏，正文加载在窗口内显示进度，双击后不必等正文下载完才出现
        _ = _viewModel.OpenAsync(emailId);
    }

    private Views.MailReaderWindow EnsureWindow()
    {
        if (_window is { } existing) return existing;

        var window = new Views.MailReaderWindow(_viewModel);

        // Owner 必须在 Show 之前设置：阅读窗口不占任务栏，始终跟随主窗口（子窗体语义）
        if (System.Windows.Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        window.Closed += (_, _) =>
        {
            _window = null;
            // 视图模型是单例：关窗后清空正文，别让最后一封邮件一直占着内存
            _viewModel.Reset();
        };
        _window = window;
        return window;
    }
}
