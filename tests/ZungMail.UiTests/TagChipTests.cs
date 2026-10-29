using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 标签胶囊：实心高饱和底色 + 白色粗体，字号比原来大一档；
/// 列表、主窗口阅读区、独立阅读窗口共用同一套样式，不再用半透明描边款。
/// </summary>
[Trait("Category", "Ui")]
public sealed class TagChipTests
{
    private static string ControlsXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Theme", "Controls.xaml"));

    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));

    private static string ReaderWindowXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MailReaderWindow.xaml"));

    [Fact]
    public void 主题里有实心白字标签样式()
    {
        var xaml = ControlsXaml;

        Assert.Contains("x:Key=\"TagChip\"", xaml);
        Assert.Contains("x:Key=\"TagChipText\"", xaml);
        // 字号 14（用户要求再大一号）
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"14\" />", xaml);
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"White\" />", xaml);
        Assert.Contains("<Setter Property=\"FontWeight\" Value=\"Bold\" />", xaml);
        // 胶囊形状：固定高度 26、圆角取一半（13），两端才是半圆；最小宽度 64 让它更显眼
        Assert.Contains("<Setter Property=\"Height\" Value=\"26\" />", xaml);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"64\" />", xaml);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"13\" />", xaml);
        // 长度也要撑开一点：左右内边距 13
        Assert.Contains("<Setter Property=\"Padding\" Value=\"13,0\" />", xaml);
    }

    [Fact]
    public void 列表标签胶囊默认至少64宽且高26()
    {
        // 单标签用 TagChip 样式（样式里已含 MinWidth 60 / Height 22），多标签分段胶囊自带 Height=22
        Assert.Contains("Style=\"{StaticResource TagChip}\"", MainWindowXaml);
        Assert.Contains("Height=\"26\"", MainWindowXaml);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"64\" />", ControlsXaml);
        Assert.Contains("FontSize=\"14\"", MainWindowXaml);
    }

    [Fact]
    public void 三处标签都套了同一套胶囊()
    {
        // 阅读区（主窗口 + 独立阅读窗口）仍是实心白字胶囊；
        // 列表行 046 起改成「一个中性胶囊 + 每个名字用自己的标签色」，见 TagPieces 相关用例
        Assert.True(Regex.Matches(MainWindowXaml, "Style=\"\\{StaticResource TagChip\\}\"").Count >= 1,
            "主窗口阅读区应仍使用 TagChip");
        Assert.Contains("Style=\"{StaticResource TagChip}\"", ReaderWindowXaml);
        Assert.Contains("ItemsSource=\"{Binding TagPieces}\"", MainWindowXaml);
    }

    [Fact]
    public void 标签不再用低透明度底色与本色文字()
    {
        // 列表多标签段用「实心标签色底 + 白字」，不再用「本色文字」也不叠半透明描边；
        // 阅读区两处同样是白字实心胶囊
        Assert.DoesNotContain("Foreground=\"{Binding Color, Converter={StaticResource HexToBrush}}\"", MainWindowXaml);
        Assert.Contains("Background=\"{Binding Color, Converter={StaticResource HexToBrush}}\"", MainWindowXaml);
        Assert.DoesNotContain("Foreground=\"{Binding Color, Converter={StaticResource HexToBrush}}\"", ReaderWindowXaml);
        Assert.DoesNotContain("Opacity=\"0.35\"", MainWindowXaml);
        Assert.DoesNotContain("Opacity=\"0.35\"", ReaderWindowXaml);
    }
}
