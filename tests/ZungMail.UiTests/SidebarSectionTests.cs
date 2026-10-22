using System.IO;
using System.Text.RegularExpressions;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 左侧栏「快捷视图 / 标签」分区：要有卡片边界（SidebarCard）与头部折展按钮，
/// 折叠状态记进设置，重启后保持。
/// </summary>
[Trait("Category", "Ui")]
public sealed class SidebarSectionTests
{
    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));

    [Fact]
    public void 两个分区都套了卡片边界()
    {
        var xaml = MainWindowXaml;

        Assert.Contains("x:Key=\"SidebarCard\"", xaml);

        var cards = Regex.Matches(xaml, "Style=\"\\{StaticResource SidebarCard\\}\"");
        Assert.True(cards.Count >= 2, $"「快捷视图」「标签」都应是卡片，实际只有 {cards.Count} 处");

        // 卡片要有描边与圆角，否则看不出边界
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"8\" />", xaml);
        Assert.Contains("Value=\"{DynamicResource RuleBrush}\"", xaml);
    }

    [Fact]
    public void 两个分区都有折展入口与折叠绑定()
    {
        var xaml = MainWindowXaml;

        Assert.Contains("ToggleQuickViewsCommand", xaml);
        Assert.Contains("ToggleTagsCommand", xaml);
        Assert.Contains("IsQuickViewsExpanded", xaml);
        Assert.Contains("IsTagsExpanded", xaml);

        // 箭头旋转必须写在 Style 的 Setter/DataTrigger 里：元素上的本地值会压制触发器
        Assert.DoesNotContain("<Path Grid.Column=\"0\" Data=\"{StaticResource IcoChevron}\" RenderTransform=", xaml);
    }

    [Fact]
    public void 折叠状态默认展开并随设置保存()
    {
        var settings = new AppSettings();

        Assert.True(settings.SidebarQuickViewsExpanded);
        Assert.True(settings.SidebarTagsExpanded);

        settings.SidebarQuickViewsExpanded = false;
        settings.SidebarTagsExpanded = false;
        var clone = settings.Clone();

        Assert.False(clone.SidebarQuickViewsExpanded);
        Assert.False(clone.SidebarTagsExpanded);
    }
}
