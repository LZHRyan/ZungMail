using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>邮件头的抄送 / 密送地址要和收件人一样可交互（悬停高亮、左键卡片、右键菜单）。</summary>
[Trait("Category", "Ui")]
public sealed class HeaderAddressChipsTests
{
    private static string ReadMainWindow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml");
        Assert.True(File.Exists(path), "缺少主窗口 XAML：" + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void 共用地址交互模板存在且被三行引用()
    {
        var xaml = ReadMainWindow();

        Assert.Contains("x:Key=\"AddressChipTemplate\"", xaml);
        Assert.Contains("OnAddressChipClick", xaml);
        // 模板本身 + 抄送 + 密送（收件人行仍是内联模板，单独断言绑定即可）
        Assert.True(xaml.Split("AddressChipTemplate").Length - 1 >= 3, "地址模板应被抄送 / 密送行引用");
        Assert.Contains("{Binding CcAddresses}", xaml);
        Assert.Contains("{Binding BccAddresses}", xaml);
    }

    [Fact]
    public void 抄送不再退化成纯文本行()
    {
        var xaml = ReadMainWindow();

        Assert.DoesNotContain("Detail.CcDisplay", xaml);
        Assert.DoesNotContain("Detail.BccDisplay", xaml);
    }
}
