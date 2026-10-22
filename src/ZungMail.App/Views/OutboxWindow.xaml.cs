using System.Windows;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Views;

/// <summary>发件箱窗口（FR-08）。</summary>
public partial class OutboxWindow : DialogWindow
{
    private readonly OutboxViewModel _viewModel;

    public OutboxWindow(OutboxViewModel viewModel)
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
}
