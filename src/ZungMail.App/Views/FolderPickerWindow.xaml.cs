using System.Windows;
using System.Windows.Input;
using ZungMail.App.Services;

namespace ZungMail.App.Views;

/// <summary>“移动到文件夹”选择窗口。</summary>
public partial class FolderPickerWindow : DialogWindow
{
    private readonly FolderPickerViewModel _viewModel;

    public FolderPickerWindow(FolderPickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        DialogResult = result;
        Close();
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Selected is null) return;
        _viewModel.ConfirmCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        base.OnClosed(e);
    }
}
