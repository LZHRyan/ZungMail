using System.Windows;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Views;

/// <summary>等待回复管理窗口。</summary>
public partial class FollowUpWindow : DialogWindow
{
    private readonly FollowUpViewModel _viewModel;

    public FollowUpWindow(FollowUpViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += OnCloseRequested;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // 窗口已关闭时忽略
        }

        Close();
    }
}
