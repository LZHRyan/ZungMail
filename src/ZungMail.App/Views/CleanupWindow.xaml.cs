using System.Windows;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Views;

/// <summary>历史邮件清理窗口：筛选预览、一键 / 勾选清理、进度与审计记录。</summary>
public partial class CleanupWindow : DialogWindow
{
    private readonly CleanupViewModel _viewModel;

    public CleanupWindow(CleanupViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += OnCloseRequested;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closed += (_, _) => viewModel.Detach();
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        DialogResult = result;
    }

    private void OnPreviewItemChecked(object sender, RoutedEventArgs e)
    {
        _viewModel.RecalculateSelection();
    }
}
