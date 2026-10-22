using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 仿冒域名（钓鱼）警示的界面契约：列表红色角标、阅读区与独立阅读窗口的红色警示条排在陌生提示之前、
/// 设置页有开关与容差。直接解析 XAML，防止以后改界面时丢掉。
/// </summary>
[Trait("Category", "Ui")]
public sealed class PhishingDomainUiTests
{
    private static string ReadView(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", fileName));

    private static string MainWindow => ReadView("MainWindow.xaml");

    [Fact]
    public void 列表行有疑似钓鱼角标且用危险色()
    {
        var xaml = MainWindow;
        var rowStart = xaml.IndexOf("x:Key=\"MailRowTemplate\"", StringComparison.Ordinal);
        Assert.True(rowStart > 0, "找不到 MailRowTemplate");
        var rowEnd = xaml.IndexOf("</DataTemplate>", rowStart, StringComparison.Ordinal);
        var rowTemplate = xaml[rowStart..rowEnd];

        Assert.Contains("PhishingBadgeVisible", rowTemplate);
        Assert.Contains("疑似钓鱼", rowTemplate);
        Assert.Contains("CriticalBrush", rowTemplate);
    }

    [Fact]
    public void 主窗口阅读区有钓鱼警示条且排在陌生提示之前()
    {
        var xaml = MainWindow;

        Assert.Contains("ShowDetailPhishing", xaml);
        Assert.Contains("警惕钓鱼 · 发件人域名与你自己的域名高度相似", xaml);
        Assert.Contains("DetailPhishingHint", xaml);

        var phishing = xaml.IndexOf("ShowDetailPhishing", StringComparison.Ordinal);
        var stranger = xaml.IndexOf("ShowDetailStranger", StringComparison.Ordinal);
        Assert.True(phishing >= 0 && stranger >= 0);
        Assert.True(phishing < stranger, "红色钓鱼警示应排在琥珀陌生提示之前");
    }

    [Fact]
    public void 独立阅读窗口同样有钓鱼警示条()
    {
        var xaml = ReadView("MailReaderWindow.xaml");

        Assert.Contains("ShowDetailPhishing", xaml);
        Assert.Contains("警惕钓鱼 · 发件人域名与你自己的域名高度相似", xaml);
        Assert.Contains("CriticalBrush", xaml);
    }

    [Fact]
    public void 设置页有仿冒域名开关与容差()
    {
        var xaml = ReadView("SettingsWindow.xaml");

        Assert.Contains("PhishingDomainWarning", xaml);
        Assert.Contains("检测仿冒域名（钓鱼警示）", xaml);
        Assert.Contains("PhishingToleranceIndex", xaml);
        Assert.Contains("PhishingToleranceOptions", xaml);
        Assert.Contains("放宽后更容易误报", xaml);
    }
}
