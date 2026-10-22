using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>搜索框在工具栏（不在标题栏），标题栏只留品牌与窗口按钮并收紧高度。</summary>
[Trait("Category", "Ui")]
public sealed class SearchBoxPlacementTests
{
    private static string ReadMainWindow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml");
        Assert.True(File.Exists(path), "缺少主窗口 XAML：" + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void 搜索框在工具栏行内_标题栏收窄到38()
    {
        var xaml = ReadMainWindow();

        Assert.Contains("搜索框：从标题栏移到工具栏里", xaml);
        Assert.Contains("Height=\"38\"", xaml);
        Assert.Contains("<RowDefinition Height=\"Auto\" />", xaml);

        // SearchBox 只声明一次，且位于工具栏注释之后（标题栏只保留弹层宿主）
        Assert.Equal(1, xaml.Split("x:Name=\"SearchBox\"").Length - 1);

        var toolbarIndex = xaml.IndexOf("搜索框：从标题栏移到工具栏里", System.StringComparison.Ordinal);
        var searchIndex = xaml.IndexOf("x:Name=\"SearchBox\"", System.StringComparison.Ordinal);
        Assert.True(toolbarIndex > 0 && searchIndex > toolbarIndex, "SearchBox 应在工具栏注释之后");
        Assert.Contains("x:Name=\"SearchFilterToggle\"", xaml.Substring(toolbarIndex, 6000));

        // @ 语法弹层仍在窗口里（宿住在标题栏的隐藏 Grid 中），名字域不变
        Assert.Contains("x:Name=\"SearchSyntaxPopup\"", xaml);
    }
}
