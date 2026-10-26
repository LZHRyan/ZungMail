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
        // 加大一号：原来 10.5
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"11.5\" />", xaml);
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"White\" />", xaml);
        Assert.Contains("<Setter Property=\"FontWeight\" Value=\"Bold\" />", xaml);
        // 胶囊形状：固定高度 20、圆角取一半（10），两端才是半圆
        Assert.Contains("<Setter Property=\"Height\" Value=\"20\" />", xaml);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"10\" />", xaml);
        // 长度也要撑开一点：左右内边距 11
        Assert.Contains("<Setter Property=\"Padding\" Value=\"11,0\" />", xaml);
    }

    [Fact]
    public void 三处标签都套了同一套胶囊()
    {
        Assert.True(Regex.Matches(MainWindowXaml, "Style=\"\\{StaticResource TagChip\\}\"").Count >= 2,
            "主窗口的列表与阅读区都应使用 TagChip");
        Assert.Contains("Style=\"{StaticResource TagChip}\"", ReaderWindowXaml);
    }

    [Fact]
    public void 标签不再用低透明度底色与本色文字()
    {
        // 文字颜色不再跟标签色走（改成白色），也不再叠两层半透明描边
        Assert.DoesNotContain("Foreground=\"{Binding Color, Converter={StaticResource HexToBrush}}\"", MainWindowXaml);
        Assert.DoesNotContain("Foreground=\"{Binding Color, Converter={StaticResource HexToBrush}}\"", ReaderWindowXaml);
        Assert.DoesNotContain("Opacity=\"0.35\"", MainWindowXaml);
        Assert.DoesNotContain("Opacity=\"0.35\"", ReaderWindowXaml);
    }
}
