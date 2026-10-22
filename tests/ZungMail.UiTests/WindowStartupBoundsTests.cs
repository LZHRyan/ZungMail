using System;
using System.Windows;
using ZungMail.App.Views;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 主窗口启动尺寸：占显示器工作区宽高的 80% 并居中，且以 MinWidth/MinHeight 兜底、不超过工作区。
/// 覆盖 4K/2K/1080P 小屏与「最小尺寸比屏幕还大」的极端情况。
/// </summary>
[Trait("Category", "Ui")]
public sealed class WindowStartupBoundsTests
{
    [Fact]
    public void UsesEightyPercentOfWorkAreaCentered()
    {
        var workArea = new Rect(0, 0, 2560, 1667);

        var bounds = WindowStartupBounds.ComputeBounds(workArea, 1080, 640, WindowStartupBounds.DefaultOccupancy);

        Assert.Equal(2048, bounds.Width, 3);
        Assert.Equal(1333.6, bounds.Height, 3);
        Assert.Equal(256, bounds.X, 3);
        Assert.Equal(166.7, bounds.Y, 3);
    }

    [Fact]
    public void KeepsTaskbarOffsetWhenWorkAreaIsNotAtOrigin()
    {
        // 任务栏在左侧：工作区 X 偏移，窗口仍然要在工作区内居中
        var workArea = new Rect(120, 0, 1800, 1000);

        var bounds = WindowStartupBounds.ComputeBounds(workArea, 800, 500, 0.8);

        Assert.Equal(120 + 180, bounds.X, 3);
        Assert.Equal(1440, bounds.Width, 3);
        Assert.Equal(800, bounds.Height, 3);
        Assert.Equal(100, bounds.Y, 3);
    }

    [Fact]
    public void FallsBackToMinimumSizeOnSmallScreens()
    {
        // 1366×768 笔记本：80% 只有 1092×582，高度低于 MinHeight，按最小高度兜底
        var workArea = new Rect(0, 0, 1366, 728);

        var bounds = WindowStartupBounds.ComputeBounds(workArea, 1080, 640, 0.8);

        Assert.Equal(1092.8, bounds.Width, 3);
        Assert.Equal(640, bounds.Height, 3);
    }

    [Fact]
    public void NeverExceedsWorkArea()
    {
        // 最小宽高比屏幕还大（远程桌面小窗口）：最多铺满工作区
        var workArea = new Rect(0, 0, 800, 600);

        var bounds = WindowStartupBounds.ComputeBounds(workArea, 1080, 640, 0.8);

        Assert.Equal(800, bounds.Width, 3);
        Assert.Equal(600, bounds.Height, 3);
        Assert.Equal(0, bounds.X, 3);
        Assert.Equal(0, bounds.Y, 3);
    }

    [Fact]
    public void ReturnsEmptyForInvalidWorkArea()
    {
        Assert.True(WindowStartupBounds.ComputeBounds(new Rect(0, 0, 0, 0), 1080, 640, 0.8).IsEmpty);
        Assert.True(WindowStartupBounds.ComputeBounds(new Rect(0, 0, 1000, 800), 1080, 640, 0).IsEmpty);
    }
}
