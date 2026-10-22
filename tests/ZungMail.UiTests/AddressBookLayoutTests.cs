using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>通讯录窗口：搜索框要像搜索控件（放大镜 / 水印 / 清空），列表要带分页栏。</summary>
[Trait("Category", "Ui")]
public sealed class AddressBookLayoutTests
{
    private static string ReadXaml()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "AddressBookWindow.xaml");
        Assert.True(File.Exists(path), "缺少通讯录 XAML：" + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void 搜索框_带放大镜图标水印与清空按钮()
    {
        var xaml = ReadXaml();

        Assert.Contains("x:Name=\"ContactSearchBox\"", xaml);
        Assert.Contains("IcoSearch", xaml);
        Assert.Contains("搜索姓名或邮箱", xaml);
        Assert.Contains("ClearSearchCommand", xaml);
        Assert.Contains("NotEmptyToVisibility", xaml);
    }

    [Fact]
    public void 列表_带分页栏与翻页命令()
    {
        var xaml = ReadXaml();

        Assert.Contains("PreviousPageCommand", xaml);
        Assert.Contains("NextPageCommand", xaml);
        Assert.Contains("{Binding PageInfo}", xaml);
        Assert.Contains("ShowPager", xaml);
    }
}
