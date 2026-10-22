using System;
using System.Windows;
using ZungMail.App.ViewModels;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;

namespace ZungMail.App.Services;

/// <summary>
/// 升级窗口的生命周期：单实例复用，关闭后释放视图模型的订阅。
/// 入口（状态栏 / 设置 / 托盘）只调用 <see cref="Show"/>，不直接 new 窗口。
/// </summary>
public sealed class UpdateWindowService
{
    private readonly UpdateCoordinator _coordinator;
    private readonly UpdateInstallService _install;
    private readonly IDialogService _dialogs;
    private readonly IUpdateService _updateService;
    private readonly IApplicationVersion _applicationVersion;

    private UpdateWindow? _window;

    public UpdateWindowService(
        UpdateCoordinator coordinator,
        UpdateInstallService install,
        IDialogService dialogs,
        IUpdateService updateService,
        IApplicationVersion applicationVersion)
    {
        _coordinator = coordinator;
        _install = install;
        _dialogs = dialogs;
        _updateService = updateService;
        _applicationVersion = applicationVersion;
    }

    public void Show()
    {
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Focus();
            return;
        }

        var viewModel = new UpdateViewModel(
            _coordinator,
            _install,
            _dialogs,
            _updateService,
            _applicationVersion);

        var window = new UpdateWindow { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => window.Close();
        window.Closed += (_, _) =>
        {
            viewModel.Dispose();
            _window = null;
        };

        // 主窗口可见时作为属主：升级窗口跟随主窗口层级，不会跑到别的程序后面
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        _window = window;
        window.Show();
        window.Activate();
    }
}
