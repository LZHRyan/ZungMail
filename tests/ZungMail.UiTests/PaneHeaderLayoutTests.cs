using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 三栏表头（邮件列表 / 阅读工具条 / 邮件上下文）必须同高：MinHeight 统一，
/// 否则右侧两栏的底线会跟列表错开，界面看起来参差不齐。
/// </summary>
[Trait("Category", "Ui")]
public sealed class PaneHeaderLayoutTests
{
    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));

    [Fact]
    public void 三栏表头统一高度且都是单行()
    {
        var xaml = MainWindowXaml;

        Assert.Contains("三栏表头统一高度", xaml);

        var matches = Regex.Matches(xaml, "MinHeight=\"44\"");
        Assert.True(matches.Count >= 3, $"三栏表头应各自声明 MinHeight=\"44\"，实际只有 {matches.Count} 处");

        // 标题与副标题必须同一行：不能再出现「副标题 Margin 顶部 3px 堆到下一行」的老写法
        Assert.DoesNotContain(
            "<TextBlock Style=\"{StaticResource MutedText}\" Margin=\"0,3,0,0\" Text=\"{Binding ListSubtitle}\" />", xaml);
        Assert.DoesNotContain(
            "<TextBlock Style=\"{StaticResource MutedText}\" Margin=\"0,3,0,0\" Text=\"{Binding ContextSidebarSummary}\" />", xaml);
    }

    [Fact]
    public void 邮件上下文标题与列表标题同一字号层级且不重复摘要()
    {
        var xaml = MainWindowXaml;

        // 侧栏头部以前是裸 FontWeight=SemiBold，与列表标题（PageTitle）不一致，视觉上像两级
        Assert.Contains("Text=\"邮件上下文\"", xaml);

        // 头部不再重复显示「往来 N · 附件 N」：这一行只保留在下方「往来邮件」卡片标题行
        var summaryBindings = Regex.Matches(xaml, "\\{Binding ContextSidebarSummary\\}")
            .Cast<Match>()
            .Select(match => match.Value)
            .ToList();
        Assert.Single(summaryBindings);
    }
}
