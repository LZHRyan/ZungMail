using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 陌生发件人标识的界面契约：列表有角标、阅读区与独立阅读窗口有警告条、
/// 阅读区能一键加入通讯录、设置页有开关。直接读 XAML，防止以后改界面时把它丢了。
/// </summary>
[Trait("Category", "Ui")]
public sealed class StrangerSenderUiTests
{
    private static string ReadView(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", fileName));

    private static string MainWindow => ReadView("MainWindow.xaml");

    [Fact]
    public void 列表行有陌生角标并使用警告色()
    {
        var xaml = MainWindow;

        Assert.Contains("StrangerBadgeVisible", xaml);
        Assert.Contains("Text=\"陌生\"", xaml);
        Assert.Contains("WarningSoftBrush", xaml);
        Assert.Contains("WarningBrush", xaml);

        // 角标要挂在列表行模板里，而不是别处的提示
        var rowStart = xaml.IndexOf("x:Key=\"MailRowTemplate\"", StringComparison.Ordinal);
        Assert.True(rowStart > 0, "找不到 MailRowTemplate");
        var rowEnd = xaml.IndexOf("</DataTemplate>", rowStart, StringComparison.Ordinal);
        var rowTemplate = xaml[rowStart..rowEnd];

        Assert.Contains("StrangerBadgeVisible", rowTemplate);
        Assert.Contains("WarningSoftBrush", rowTemplate);
    }

    [Fact]
    public void 阅读区有陌生发件人提示条与加入通讯录入口()
    {
        var xaml = MainWindow;

        Assert.Contains("ShowDetailStranger", xaml);
        Assert.Contains("陌生发件人 · 首次收到该地址来信", xaml);
        Assert.Contains("JoinStrangerContactsCommand", xaml);
        Assert.Contains("加入通讯录", xaml);
    }

    [Fact]
    public void 独立阅读窗口同样有陌生提示条()
    {
        var xaml = ReadView("MailReaderWindow.xaml");

        Assert.Contains("ShowDetailStranger", xaml);
        Assert.Contains("陌生发件人 · 首次收到该地址来信", xaml);
        Assert.Contains("WarningSoftBrush", xaml);
    }

    [Fact]
    public void 设置页有标记陌生发件人开关()
    {
        var xaml = ReadView("SettingsWindow.xaml");

        Assert.Contains("MarkStrangerSenders", xaml);
        Assert.Contains("标记陌生发件人", xaml);
    }

    [Fact]
    public void 无边框窗口用到的图标与转换器都在应用级资源里()
    {
        // 独立阅读窗口是另一个 Window：只允许用 App 级资源，避免 XamlParseException
        var app = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml"));
        Assert.Contains("Icons.xaml", app);
        Assert.Contains("InverseBoolToVisibility", app);
    }
}
