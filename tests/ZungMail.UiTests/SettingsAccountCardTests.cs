using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 设置页的「邮箱账户」列表（账户页 / 签名页）：套卡片样式——圆角 + 1px 描边容器，
/// 条目用卡片条目（透明底、悬停浅底、选中强调色描边），保证与其他页面的卡片观感一致。
/// </summary>
[Trait("Category", "Ui")]
public sealed class SettingsAccountCardTests
{
    private static string SettingsXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml"));

    private static string ControlsXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Theme", "Controls.xaml"));

    [Fact]
    public void 主题里有卡片容器与卡片条目样式()
    {
        var xaml = ControlsXaml;

        Assert.Contains("x:Key=\"CardPanel\"", xaml);
        Assert.Contains("x:Key=\"CardListItem\"", xaml);

        // 圆角 + 描边才有卡片边界
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"10\" />", xaml);
        Assert.Contains("<Setter Property=\"BorderBrush\" Value=\"{DynamicResource RuleBrush}\" />", xaml);

        // 选中用强调色描边，避免和普通悬停混在一起
        Assert.Contains("Property=\"BorderBrush\" Value=\"{DynamicResource AccentBrush}\"", xaml);
        Assert.Contains("Property=\"Background\" Value=\"{DynamicResource AccentSoftBrush}\"", xaml);
    }

    [Fact]
    public void 账户页与签名页都用卡片装账户列表()
    {
        var xaml = SettingsXaml;

        var panels = Regex.Matches(xaml, "Style=\"\\{StaticResource CardPanel\\}\"");
        Assert.True(panels.Count >= 2, $"账户页与签名页各一张卡片，实际只有 {panels.Count} 处");

        var items = Regex.Matches(xaml, "StaticResource CardListItem");
        Assert.True(items.Count >= 2, $"两张卡片的条目都应套 CardListItem，实际只有 {items.Count} 处");
    }

    [Fact]
    public void 卡片头部与列表同处一张卡片()
    {
        var xaml = SettingsXaml;

        // 账户页与签名页的标题行（原生在卡片外）现在都在卡片里
        Assert.Contains("Text=\"邮箱账户（FR-01）\"", xaml);
        Assert.Contains("Text=\"邮箱账户\"", xaml);

        // 头部与列表之间的分隔线：卡片式布局的标志
        Assert.Contains("BorderThickness=\"0,0,0,1\"", xaml);
    }

    [Fact]
    public void 签名页不再用无边界的分组条目()
    {
        var xaml = SettingsXaml;

        // 签名列表（SignatureAccounts）必须走卡片条目，不能再退回 PanelListItem
        var signatureList = xaml[xaml.IndexOf("ItemsSource=\"{Binding SignatureAccounts}\"", System.StringComparison.Ordinal)..];
        var listEnd = signatureList.IndexOf("</ListBox>", System.StringComparison.Ordinal);
        Assert.True(listEnd > 0, "找不到签名账户列表");
        Assert.DoesNotContain("PanelListItem", signatureList[..listEnd]);
    }
}
