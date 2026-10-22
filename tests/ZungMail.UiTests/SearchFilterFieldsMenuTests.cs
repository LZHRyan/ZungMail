using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>「+」菜单：像 Foxmail 一样勾选要显示哪些搜索条件，选择要能保存。</summary>
[Trait("Category", "Ui")]
public sealed class SearchFilterFieldsMenuTests
{
    private static string ReadMainWindow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml");
        Assert.True(File.Exists(path), "缺少主窗口 XAML：" + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void 加号菜单含九个可勾选条件且各自绑定可见性()
    {
        var xaml = ReadMainWindow();

        Assert.Contains("x:Name=\"SearchFilterFieldsMenuButton\"", xaml);
        Assert.Contains("配置显示哪些搜索条件", xaml);

        // 左键也要能打开菜单：ContextMenu 默认只响应右键，必须挂 Click 手动 IsOpen
        Assert.Contains("Click=\"OnSearchFilterFieldsMenuClick\"", xaml);

        foreach (var key in new[] { "From", "To", "Subject", "Body", "Attachment", "Tag", "Reminder", "Note", "Time" })
        {
            Assert.Contains($"IsChecked=\"{{Binding ShowFilter{key}, Mode=TwoWay}}\"", xaml);
            Assert.Contains($"{{Binding ShowFilter{key}, Converter={{StaticResource BoolToVisibility}}}}", xaml);
        }

        // 条件单元自动换行：隐藏某项后不会留下空格子
        Assert.Contains("<WrapPanel Orientation=\"Horizontal\">", xaml);

        // 勾选状态：勾选项显示对勾（IsCheckable 才占检查列，普通菜单不位移）
        Assert.Contains("x:Name=\"CheckSlot\"", xaml);
        Assert.Contains("x:Name=\"CheckMark\"", xaml);
        Assert.Contains("Property=\"IsCheckable\" Value=\"True\"", xaml);
        Assert.Contains("Property=\"IsChecked\" Value=\"True\"", xaml);
        Assert.Contains("Data=\"{StaticResource IcoCheck}\"", xaml);
    }
}
