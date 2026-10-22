using System.Windows;
using ZungMail.Core.Abstractions;

namespace ZungMail.App.Views;

/// <summary>收发诊断结果窗口：逐阶段展示结论，并可复制报告。</summary>
public partial class DiagnosticsWindow : DialogWindow
{
    private readonly DiagnosticsReport _report;

    public DiagnosticsWindow(DiagnosticsReport report)
    {
        InitializeComponent();
        _report = report;

        TitleText.Text = $"收发诊断 · {report.AccountEmail}";
        SummaryText.Text = report.Summary;
        StepList.ItemsSource = report.Steps;
        StatusText.Text = $"生成于 {report.GeneratedUtc.ToLocalTime():HH:mm:ss}";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_report.ToText());
            StatusText.Text = "报告已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }
}
