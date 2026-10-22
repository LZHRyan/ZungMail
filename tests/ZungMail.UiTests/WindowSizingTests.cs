using System.Windows;
using ZungMail.App.Views;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>写信 / 编辑邮件窗口默认尺寸：当前主窗体宽高的 85%，并受最小尺寸兜底。</summary>
[Trait("Category", "Ui")]
public class WindowSizingTests
{
    [Fact]
    public void 写信窗口比例_固定为百分之八十五()
        => Assert.Equal(0.85, WindowSizing.ComposeOwnerRatio);

    [Fact]
    public void 阅读窗口比例_同样为百分之八十五()
        => Assert.Equal(0.85, WindowSizing.ReaderOwnerRatio);

    [Theory]
    [InlineData(1600, 1000, 1360, 850)]
    [InlineData(1400, 880, 1190, 748)]
    public void 阅读窗口尺寸_按主窗体宽高取85并受最小尺寸兜底(double ownerWidth, double ownerHeight, double expectedWidth, double expectedHeight)
    {
        var size = WindowSizing.OwnerRelative(
            ownerWidth, ownerHeight, 700, 440, WindowSizing.ReaderOwnerRatio);

        Assert.Equal(expectedWidth, size.Width, 3);
        Assert.Equal(expectedHeight, size.Height, 3);
    }

    [Fact]
    public void 小主窗体_阅读窗口不低于最小尺寸()
    {
        var size = WindowSizing.OwnerRelative(760, 480, 700, 440, WindowSizing.ReaderOwnerRatio);

        Assert.Equal(700, size.Width);
        Assert.Equal(440, size.Height);
    }

    [Theory]
    [InlineData(1600, 1000, 1360, 850)]
    [InlineData(2048, 1200, 1740.8, 1020)]
    [InlineData(1080, 700, 918, 595)]
    public void 比例尺寸_按主窗体实际宽高计算(double ownerWidth, double ownerHeight, double expectedWidth, double expectedHeight)
    {
        var size = WindowSizing.OwnerRelative(ownerWidth, ownerHeight, 720, 560, WindowSizing.ComposeOwnerRatio);

        Assert.Equal(expectedWidth, size.Width, 3);
        Assert.Equal(expectedHeight, size.Height, 3);
    }

    [Fact]
    public void 小主窗体_不低于写信窗口最小尺寸()
    {
        var size = WindowSizing.OwnerRelative(800, 600, 720, 560, WindowSizing.ComposeOwnerRatio);

        Assert.Equal(720, size.Width);
        Assert.Equal(560, size.Height);
    }

    [Theory]
    [InlineData(0, 900)]
    [InlineData(1200, 0)]
    [InlineData(-1, -1)]
    public void 主窗体尺寸无效_返回空尺寸(double ownerWidth, double ownerHeight)
    {
        var size = WindowSizing.OwnerRelative(ownerWidth, ownerHeight, 720, 560, WindowSizing.ComposeOwnerRatio);

        Assert.True(size.IsEmpty);
    }
}
