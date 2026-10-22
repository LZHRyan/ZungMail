using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>工具栏刷新按钮要有加载态，上下文侧边栏开关要有开/关状态与直观图标。</summary>
[Trait("Category", "Ui")]
public sealed class ToolbarStateTests
{
    private static string ReadMainWindow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml");
        Assert.True(File.Exists(path), "缺少主窗口 XAML：" + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void 刷新按钮_同步中显示旋转的加载弧()
    {
        var xaml = ReadMainWindow();

        Assert.Contains("IcoSpinner", xaml);
        Assert.Contains("SpinRefreshStory", xaml);
        Assert.Contains("RepeatBehavior=\"Forever\"", xaml);
        Assert.Contains("{Binding RefreshToolTip}", xaml);
    }

    [Fact]
    public void 上下文侧边栏_用图标开关并体现开关状态()
    {
        var xaml = ReadMainWindow();
        var icons = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Theme", "Icons.xaml"));
        var controls = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Theme", "Controls.xaml"));

        Assert.Contains("IcoPanelRight", icons);
        Assert.Contains("x:Key=\"IconToggleBtn\"", controls);
        Assert.Contains("Property=\"IsChecked\" Value=\"True\"", controls);
        Assert.Contains("AccentSoftBrush", controls);
        Assert.Contains("IconToggleBtn", xaml);
        Assert.Contains("ToggleContextSidebarCommand", xaml);
        Assert.Contains("{Binding ContextSidebarToggleHint}", xaml);
        // 侧边栏按钮不再是普通的「人形」图标按键：图标换成右栏示意
        Assert.Contains("IcoPanelRight", xaml);
    }
}
