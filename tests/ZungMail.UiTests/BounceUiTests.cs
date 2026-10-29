using System;
using System.IO;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 退信的界面契约：列表「退信」角标排在「陌生」之前、两处阅读区都有退信提示条、
/// 设置页有「标记退信邮件」开关且默认开启。
/// </summary>
[Trait("Category", "Ui")]
public sealed class BounceUiTests
{
    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));

    private static string ReaderWindowXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MailReaderWindow.xaml"));

    private static string SettingsXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml"));

    [Fact]
    public void 列表有退信角标且排在陌生之前()
    {
        var xaml = MainWindowXaml;

        var bounce = xaml.IndexOf("Text=\"退信\"", StringComparison.Ordinal);
        var stranger = xaml.IndexOf("Text=\"陌生\"", StringComparison.Ordinal);

        Assert.True(bounce > 0, "列表缺少「退信」角标");
        Assert.True(stranger > 0, "列表缺少「陌生」角标");
        Assert.True(bounce < stranger, "「退信」角标应排在「陌生」之前");
        Assert.Contains("BounceBadgeVisible", xaml);
        // 角标自身的 ToolTip 已合并进整行提示（MailRow.RowTooltip）：这里守住「列表不再各挂 BounceHint」
        Assert.DoesNotContain("ToolTip=\"{Binding BounceHint}\"", xaml);
    }

    [Fact]
    public void 主窗口阅读区有退信提示条()
    {
        var xaml = MainWindowXaml;

        Assert.Contains("Text=\"投递失败 · 这封是退信\"", xaml);
        Assert.Contains("ShowDetailBounce", xaml);
        Assert.Contains("DetailBounceLine", xaml);
    }

    [Fact]
    public void 独立阅读窗口也有退信提示条()
    {
        var xaml = ReaderWindowXaml;

        Assert.Contains("Text=\"投递失败 · 这封是退信\"", xaml);
        Assert.Contains("ShowDetailBounce", xaml);
        Assert.Contains("DetailBounceLine", xaml);
    }

    [Fact]
    public void 设置页有退信开关()
    {
        Assert.Contains("MarkBounceMails", SettingsXaml);
        Assert.True(new AppSettings().MarkBounceMails);
    }
}
