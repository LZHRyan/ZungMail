using System.Windows;
using System.Windows.Controls;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Controls;

/// <summary>AI 摘要卡片（主窗口右栏与独立阅读窗口共用）。剪贴板操作放在代码后置，视图模型保持可测。</summary>
public partial class MailSummaryCard : UserControl
{
    public MailSummaryCard()
    {
        InitializeComponent();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MailSummaryViewModel viewModel) return;

        try
        {
            var text = viewModel.BuildCopyText();
            if (text.Length == 0) return;

            Clipboard.SetText(text);
            viewModel.StatusText = "摘要已复制到剪贴板";
        }
        catch (Exception ex)
        {
            // 剪贴板被其它进程占用时 SetText 会抛，提示即可
            viewModel.StatusText = "复制失败：" + ex.Message;
        }
    }
}
