using System;
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
    /// 回归守护：这张卡片曾经只有 380 宽、灰底灰边、14 号标题，正文区一大片空白里几乎看不见。
    /// 现在要求「至少 380 宽 / 强调色底 + 强调色边 / 警示图标 / 16.5 号标题 / 38 高主按钮」。
    /// </summary>
    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("MailReaderWindow.xaml")]
    public void 重新获取正文卡片要显眼_宽度强调色与图标齐备(string file)
    {
        var xaml = Read(file);

        var start = xaml.IndexOf("ShowReloadBodyButton", StringComparison.Ordinal);
        Assert.True(start > 0, $"{file} 找不到重新获取正文卡片");

        // 卡片本身：往回找最近的 Border 起点，一直取到这张卡片的结束标签
        var borderStart = xaml.LastIndexOf("<Border", start, StringComparison.Ordinal);
        var cardClose = xaml.IndexOf("</Border>", start, StringComparison.Ordinal);
        Assert.True(cardClose > borderStart, $"{file} 找不到卡片结束标签");

        var tagEnd = xaml.IndexOf('>', borderStart);
        var card = xaml.Substring(borderStart, tagEnd - borderStart);
        var block = xaml.Substring(borderStart, cardClose - borderStart);

        Assert.Contains("MaxWidth=\"480\"", card);
        Assert.Contains("MinWidth=\"380\"", card);
        Assert.Contains("Padding=\"30,28\"", card);
        Assert.Contains("Background=\"{DynamicResource AccentSoftBrush}\"", card);
        Assert.Contains("BorderBrush=\"{DynamicResource AccentBrush}\"", card);

        // 卡片内容：警示图标 + 16.5 号标题 + 38 高主按钮
        Assert.Contains("Data=\"{StaticResource IcoAlert}\"", block);
        Assert.Contains("FontSize=\"16.5\"", block);
        Assert.Contains("MinHeight=\"38\"", block);
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
