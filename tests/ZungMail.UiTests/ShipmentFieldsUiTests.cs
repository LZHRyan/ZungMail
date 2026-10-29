using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 运输字段的界面契约（045）：列表徽标显示箱型箱量，详情卡显示「箱型 · 航线 · 条款 · 金额」。
/// </summary>
[Trait("Category", "Ui")]
public sealed class ShipmentFieldsUiTests
{
    private static string ViewPath(string name) => Path.Combine(AppContext.BaseDirectory, "Views", name);

    [Fact]
    public void 列表行有箱型箱量徽标()
    {
        var xaml = File.ReadAllText(ViewPath("MainWindow.xaml"));
        Assert.Contains("Text=\"{Binding BoxesText}\"", xaml);
        Assert.Contains("Visibility=\"{Binding HasBoxes, Converter={StaticResource BoolToVisibility}}\"", xaml);
    }

    [Fact]
    public void 详情卡有运输字段一行()
    {
        var xaml = File.ReadAllText(ViewPath("MainWindow.xaml"));
        Assert.Contains("Text=\"{Binding Detail.ShipmentSummary}\"", xaml);
        Assert.Contains("Visibility=\"{Binding Detail.HasShipmentFields, Converter={StaticResource BoolToVisibility}}\"", xaml);
    }

    [Fact]
    public void 行模板只保留一个整行悬停提示()
    {
        // 046：4 个行内 ToolTip 合并成容器上 1 个，内容由 MailRow.RowTooltip 一次算好
        var xaml = File.ReadAllText(ViewPath("MainWindow.xaml"));
        Assert.Contains("ToolTip=\"{Binding RowTooltip}\"", xaml);
        Assert.DoesNotContain("ToolTip=\"{Binding PhishingHint}\"", xaml);
        Assert.DoesNotContain("ToolTip=\"{Binding StrangerHint}\"", xaml);
        Assert.DoesNotContain("ToolTip=\"{Binding ReferenceTooltip}\"", xaml);
        Assert.DoesNotContain("ToolTip=\"箱型箱量\"", xaml);
    }

    [Fact]
    public void 行内标签改为单个摘要胶囊()
    {
        // 046：不再在每行里实例化 ItemsControl + WrapPanel
        var xaml = File.ReadAllText(ViewPath("MainWindow.xaml"));
        Assert.Contains("ItemsSource=\"{Binding TagPieces}\"", xaml);
        Assert.DoesNotContain("ItemsSource=\"{Binding Tags}\"", xaml);
    }
}
