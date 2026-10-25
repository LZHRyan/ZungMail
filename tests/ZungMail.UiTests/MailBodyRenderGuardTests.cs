using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 大邮件正文渲染的回归守护。
///
/// 2094374397@qq.com 收件箱那封 8.3MB 的水单邮件（正文 HTML 6.77MB）曾让客户端卡死 7.5 秒：
/// <c>NavigateToString</c> 有 2MB 硬上限，超了直接抛异常，而异常被静默吞掉后退回 WPF 富文本，
/// 富文本又把 6.77MB 的 HTML 逐元素建成 FlowDocument（实测 1.7–3.1 秒，重建两次）。
/// 这两条止损必须在正文控件里留着，否则同一个坑会再踩一次。
/// </summary>
[Trait("Category", "Ui")]
public sealed class MailBodyRenderGuardTests
{
    private static string ReadSource()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Source", "MailBodyView.xaml.cs"));

    private static string ReadXaml()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MailBodyView.xaml"));

    [Fact]
    public void 超大正文改走写文件后导航()
    {
        var source = ReadSource();

        // 判定用 Core 里的统一口径（按 UTF-8 字节数比 2MB 上限），而不是自己拍字符数
        Assert.Contains("MailBodyRenderPolicy.NeedsFileNavigation(document)", source);
        Assert.Contains("Browser.CoreWebView2.Navigate(url)", source);

        // 文件导航的虚拟主机必须登记到内核，否则页面加载不出来
        Assert.Contains("SetVirtualHostNameToFolderMapping(", source);
        Assert.Contains("BodyVirtualHost", source);
    }

    [Fact]
    public void 渲染失败要落日志而不是静默吞掉()
        => Assert.Contains("正文 HTML 渲染失败", ReadSource());

    [Fact]
    public void 超大正文不再交给WPF富文本降级()
        => Assert.Contains("MailBodyRenderPolicy.AllowRichFallback(html)", ReadSource());

    [Fact]
    public void 正文仍然渲染在WebView2里()
    {
        var xaml = ReadXaml();

        Assert.Contains("wv2:WebView2", xaml);
        Assert.Contains("x:Name=\"Browser\"", xaml);
    }
}
