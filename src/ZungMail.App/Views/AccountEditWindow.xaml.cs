using System.Windows;
using System.Windows.Controls;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Views;

/// <summary>账户配置窗口（FR-01 / FR-02）。</summary>
public partial class AccountEditWindow : DialogWindow
{
    private readonly AccountEditViewModel _viewModel;

    public AccountEditWindow(AccountEditViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // PasswordBox.Password 不支持绑定，改由事件同步到视图模型。
        SecretBox.Password = viewModel.Secret;
        viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box) _viewModel.Secret = box.Password;
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        DialogResult = result;
    }
}
