using System.IO;
using System.Windows;
using System.Windows.Input;
using ZungMail.App.Services;
using ZungMail.App.ViewModels;
using Microsoft.Win32;

namespace ZungMail.App.Views;

/// <summary>
/// 邮件源码窗口：等宽只读展示服务器原始 MIME 或本地重建的邮件。
/// 剪贴板与保存对话框留在代码后置，视图模型保持可测（不依赖 WPF 对话框）。
/// </summary>
public partial class MailSourceWindow : DialogWindow
{
    private readonly MailSourceViewModel _viewModel;

    public MailSourceWindow(MailSourceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>重新吸附到主窗口（切换邮件时也复用同一窗口）。</summary>
    public void AttachOwner()
    {
        if (System.Windows.Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, this))
        {
            Owner = owner;
        }
    }

    private void OnWindowTitleBarMouseDown(object sender, MouseButtonEventArgs e) => base.OnTitleBarMouseDown(sender, e);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_viewModel.Text.Length == 0) return;

            Clipboard.SetText(_viewModel.Text);
            _viewModel.StatusText = "源码已复制到剪贴板";
        }
        catch (Exception ex)
        {
            // 剪贴板被其它进程占用时 SetText 会抛，这里只提示，不弹错误框
            _viewModel.StatusText = "复制失败：" + ex.Message;
        }
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Snapshot is not { } snapshot) return;

        var dialog = new SaveFileDialog
        {
            Title = "另存邮件源码",
            FileName = _viewModel.SuggestedFileName,
            DefaultExt = ".eml",
            Filter = "邮件文件 (*.eml)|*.eml|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            MailSourceFileWriter.Save(snapshot.Bytes, dialog.FileName);
            _viewModel.StatusText = "已保存：" + dialog.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.StatusText = "保存失败：" + ex.Message;
        }
    }
}
