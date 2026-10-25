using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 大邮件正文的渲染止损线：8.3MB 的水单邮件（正文 HTML 6.8MB）曾让客户端卡死 7.5 秒。
/// 两条门槛在这里守住：超过 2MB 的 NavigateToString 上限改走文件导航、
/// 超过体积的 HTML 不再交给 WPF 富文本降级位面。
/// </summary>
public sealed class MailBodyRenderPolicyTests
{
    [Fact]
    public void 普通正文不写文件_直接走NavigateToString()
        => Assert.False(MailBodyRenderPolicy.NeedsFileNavigation("<html><body><p>Dear Anna,</p></body></html>"));

    [Fact]
    public void 空文档不写文件()
    {
        Assert.False(MailBodyRenderPolicy.NeedsFileNavigation(null));
        Assert.False(MailBodyRenderPolicy.NeedsFileNavigation(string.Empty));
    }

    [Fact]
    public void 超过安全字节数的大文档改走文件导航()
    {
        var document = new string('a', MailBodyRenderPolicy.NavigateToStringByteBudget + 1);

        Assert.True(MailBodyRenderPolicy.NeedsFileNavigation(document));
    }

    /// <summary>
    /// 中文按 UTF-8 三字节算：字符数只有 60 万、按字符判断会以为安全，
    /// 实际 1.8MB 已经贴近 WebView2 的 2MB 硬限制，必须提前改走文件导航。
    /// </summary>
    [Fact]
    public void 中文正文按UTF8字节数判定而不是字符数()
    {
        var document = new string('水', 600_000);

        Assert.True(document.Length < MailBodyRenderPolicy.NavigateToStringByteBudget);
        Assert.True(MailBodyRenderPolicy.NeedsFileNavigation(document));
    }

    [Fact]
    public void 小正文允许WPF富文本降级()
    {
        Assert.True(MailBodyRenderPolicy.AllowRichFallback("<p>Dear Anna,</p>"));
        Assert.True(MailBodyRenderPolicy.AllowRichFallback(null));
        Assert.True(MailBodyRenderPolicy.AllowRichFallback(
            new string('a', MailBodyRenderPolicy.RichFallbackMaxHtmlChars)));
    }

    /// <summary>
    /// 6.8MB 的正文在 WPF 里逐元素建 FlowDocument 要 1.7–3.1 秒（实测），
    /// 而且渲染失败与属性变更会各建一次——超过门槛就必须直接排纯文本。
    /// </summary>
    [Fact]
    public void 超大正文不再交给WPF富文本()
        => Assert.False(MailBodyRenderPolicy.AllowRichFallback(
            new string('a', MailBodyRenderPolicy.RichFallbackMaxHtmlChars + 1)));

    /// <summary>
    /// 真实样本：2094374397@qq.com 收件箱那封「水单 开票 SI VSLNB00637 …」的正文是
    /// 6.77MB 的单行 HTML（57 张 base64 内嵌图），两条门槛都必须命中。
    /// </summary>
    [Fact]
    public void 水单邮件量级的正文两条门槛都命中()
    {
        var html = "<html><body>" + new string('a', 6_769_676) + "</body></html>";

        Assert.False(MailBodyRenderPolicy.AllowRichFallback(html));
        Assert.True(MailBodyRenderPolicy.NeedsFileNavigation(HtmlMailDocumentBuilder.Build(html, new MailDocumentOptions())));
    }
}
