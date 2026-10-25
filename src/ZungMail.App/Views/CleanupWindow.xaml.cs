using System.Windows;
using System.Windows.Controls;
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

    /// <summary>
    /// 只认用户点击：勾选集合按 id 单独更新，不遍历列表回读行状态。
    /// 列表是虚拟化的，容器复用会产生 Checked/Unchecked 空跑事件，回读会把状态写歪。
    /// </summary>
    private void OnPreviewItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CleanupItemRow row } box)
        {
            _viewModel.SetRowSelected(row, box.IsChecked == true);
        }
    }
}
