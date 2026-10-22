using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 阅读工具条的图标按钮：图标要够大（18px 图标 / 32px 按钮）、含义要准
/// （未读→对勾、已读→信封、打印→打印机而不是回形针），且星标已加时高亮。
/// </summary>
[Trait("Category", "Ui")]
public sealed class ReaderToolbarIconTests
{
    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));

    private static string ControlsXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Theme", "Controls.xaml"));

    [Fact]
    public void 图标按钮用18像素图标()
    {
        Assert.Contains("x:Key=\"IconPathButton\"", ControlsXaml);
        Assert.Contains("<Setter Property=\"Width\" Value=\"18\" />", ControlsXaml);

        var usage = Regex.Matches(MainWindowXaml, "Style=\"\\{StaticResource IconPathButton\\}\"");
        Assert.True(usage.Count >= 4, $"阅读工具条应有 4 个 18px 图标（已读×2 / 删除 / 打印），实际 {usage.Count}");

        // 星标图标因为要按状态换色，用的是内联 Style（BasedOn IconPathButton），单独断言
        Assert.Contains("BasedOn=\"{StaticResource IconPathButton}\"", MainWindowXaml);
    }

    [Fact]
    public void 打印按钮用打印机图标而不是回形针()
    {
        var xaml = MainWindowXaml;

        Assert.Contains("Data=\"{StaticResource IcoPrint}\"", xaml);
        Assert.Contains("ToolTip=\"打印当前邮件（保留正文排版）\"", xaml);

        // 打印按钮里的图标不能还是回形针（曾经的错配：回形针表示附件语义）
        var printIndex = xaml.IndexOf("ToolTip=\"打印当前邮件（保留正文排版）\"", StringComparison.Ordinal);
        Assert.True(printIndex > 0);
        var segment = xaml[printIndex..Math.Min(printIndex + 300, xaml.Length)];
        Assert.Contains("IcoPrint", segment);
        Assert.DoesNotContain("IcoPaperclip", segment);
    }

    [Fact]
    public void 已读切换按状态换图标且星标高亮()
    {
        var xaml = MainWindowXaml;

        // 未读 → 对勾（标为已读）；已读 → 信封（标为未读）
        Assert.Contains("IsSelectedUnread", xaml);
        Assert.Contains("Data=\"{StaticResource IcoCheck}\"", xaml);
        Assert.Contains("Data=\"{StaticResource IcoMail}\"", xaml);

        // 星标已加：金色 + 加粗描边
        Assert.Contains("IsSelectedStarred", xaml);
        Assert.Contains("WarnDotBrush", xaml);
    }
}
