using System.Windows;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Views;

/// <summary>一票货案件页：邮件时间轴 + 时效节点 + 附件，支持导出资料包。</summary>
public partial class ShipmentCaseWindow : DialogWindow
{
    private readonly ShipmentCaseViewModel _viewModel;

    public ShipmentCaseWindow(ShipmentCaseViewModel viewModel)
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
            // 窗口已关闭（例如重复点击）时设置 DialogResult 会抛异常，忽略即可
        }

        Close();
    }
}
