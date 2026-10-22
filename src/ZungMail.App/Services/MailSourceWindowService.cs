using System.Windows;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Services;

/// <summary>打开「查看邮件源码」窗口的入口，便于主视图模型与阅读窗口共用并在测试里替身。</summary>
public interface IMailSourceWindowService
{
    /// <summary>查看指定邮件的源码；窗口已存在时复用并切换内容。</summary>
    void Show(long messageId, string subject);
}

/// <summary>
/// 源码窗口的单实例宿主：只开一个窗口，重复查看时替换内容，
/// 与独立阅读窗口同样的「不占任务栏、跟随主窗口」语义。
/// </summary>
public sealed class MailSourceWindowService : IMailSourceWindowService
{
    private readonly MailSourceViewModel _viewModel;
    private Views.MailSourceWindow? _window;

    public MailSourceWindowService(MailSourceViewModel viewModel) => _viewModel = viewModel;

    public void Show(long messageId, string subject)
    {
        var window = EnsureWindow();
        window.AttachOwner();

        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();

        // 先让窗口上屏，加载过程在窗口内显示「正在获取源码…」
        _ = _viewModel.OpenAsync(messageId, subject);
    }

    private Views.MailSourceWindow EnsureWindow()
    {
        if (_window is { } existing) return existing;

        var window = new Views.MailSourceWindow(_viewModel);
        window.Closed += (_, _) =>
        {
            // 关窗时取消还没回来的 IMAP 请求，避免白占连接
            _viewModel.Cancel();
            _window = null;
        };

        _window = window;
        return window;
    }
}
