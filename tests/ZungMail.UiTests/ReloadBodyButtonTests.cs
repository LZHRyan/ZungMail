using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 正文取不到时的补救入口：主窗口阅读区和独立阅读窗口都要有「重新获取正文」按钮，
/// 且必须绑到 ReloadBodyCommand（正文为空但状态为「已下载」的邮件不会再自动重拉）。
/// </summary>
[Trait("Category", "Ui")]
public sealed class ReloadBodyButtonTests
{
    private static string Read(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", name));

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("MailReaderWindow.xaml")]
    public void 正文取不到时提供重新获取正文按钮(string file)
    {
        var xaml = Read(file);

        // 只在「正文为空且不在加载中」时出现
        Assert.Contains("ShowReloadBodyButton", xaml);
        Assert.Contains("Content=\"重新获取正文\"", xaml);
        Assert.Contains("Command=\"{Binding ReloadBodyCommand}\"", xaml);
    }

    /// <summary>
    /// 回归守护：WebView2 是独立 HWND，会盖住同一层 Grid 里的 WPF 元素。
    /// 正文容器必须绑 <c>ShowBodyContent</c>（= 不在加载 **且** 不显示卡片），
    /// 否则卡片被 WebView2 挡住，用户只看到「（此邮件暂无正文内容）」，以为按钮没做。
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("MailReaderWindow.xaml")]
    public void 正文容器在显示卡片时收起以免WebView2盖住按钮(string file)
    {
        var xaml = Read(file);

        Assert.Contains(
            "Visibility=\"{Binding ShowBodyContent, Converter={StaticResource BoolToVisibility}}\"",
            xaml);
    }
}
