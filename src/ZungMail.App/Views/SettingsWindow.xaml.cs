using System.ComponentModel;
using System.Windows;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Views;

/// <summary>设置中心窗口（通用偏好 / 账户 / 模板 / 数据）。</summary>
public partial class SettingsWindow : DialogWindow
{
    private readonly SettingsViewModel _viewModel;

    /// <summary>已通过 CloseRequested 正常关闭，跳过未保存模板的二次确认。</summary>
    private bool _closing;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += OnCloseRequested;
        Closed += (_, _) => viewModel.Dispose();
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        _closing = true;
        DialogResult = result;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 直接点右上角关闭时兜底：模板有未保存修改则先询问
        if (!_closing && !_viewModel.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>设置或修改锁定密码（PasswordBox 不能直接绑定，由代码后置传入）。</summary>
    private void OnSetLockPasswordClick(object sender, RoutedEventArgs e)
    {
        var password = ClientLockPasswordBox.Password;
        ClientLockPasswordBox.Clear();
        _viewModel.EnableClientLockCommand.Execute(password);
    }

    private void OnDisableLockClick(object sender, RoutedEventArgs e)
        => _viewModel.DisableClientLockCommand.Execute(null);

    /// <summary>把可视化内容块插入编辑区光标处。</summary>
    private async void OnInsertTemplateBlockClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string html } || string.IsNullOrEmpty(html)) return;
        await TemplateBodyEditor.InsertHtmlAsync(html);
    }

    /// <summary>把占位符令牌插入编辑区光标处。</summary>
    private async void OnInsertTemplateTokenClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string token } || string.IsNullOrEmpty(token)) return;
        await TemplateBodyEditor.InsertTextAsync(token);
    }
}
