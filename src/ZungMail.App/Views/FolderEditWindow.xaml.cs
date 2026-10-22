using System.Windows;
using ZungMail.App.Services;

namespace ZungMail.App.Views;

/// <summary>新建 / 重命名文件夹对话框。</summary>
public partial class FolderEditWindow : DialogWindow
{
    private readonly FolderEditViewModel _viewModel;

    public FolderEditWindow(FolderEditViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        Loaded += (_, _) => FocusNameBox();
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        DialogResult = result;
        Close();
    }

    private void FocusNameBox()
    {
        // 名称框是首个可编辑控件；直接让用户开始输入，无需再点一次。
        if (NameBox is not null)
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        base.OnClosed(e);
    }

    /// <summary>名称框内回车直接保存；Esc 由 IsCancel 关闭且不改动文件夹。</summary>
    private void OnNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;

        e.Handled = true;
        if (_viewModel.SaveCommand.CanExecute(null))
        {
            _viewModel.SaveCommand.Execute(null);
        }
    }
}
