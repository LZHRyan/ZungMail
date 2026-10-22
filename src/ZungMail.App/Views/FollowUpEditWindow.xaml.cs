using System.Windows;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Views;

/// <summary>等待回复创建窗口。</summary>
public partial class FollowUpEditWindow : DialogWindow
{
    private readonly FollowUpEditViewModel _viewModel;

    public FollowUpEditWindow(FollowUpEditViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
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
