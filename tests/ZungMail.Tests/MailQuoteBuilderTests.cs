using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 回复 / 转发的引用块：保留原文 HTML 排版与内嵌图片。
/// 早前这里拼的是纯文本，一进写信窗口表格、加粗、颜色、图片全丢。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MailQuoteBuilderTests
{
    /// <summary>1×1 透明 PNG（文件头即真实 PNG 签名）。</summary>
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

    private static EmailDetail Detail(string html, string text = "") => new()
    {
        Subject = "Re: 1014473 MBL+HBL DEMO-REF-0001",
        FromName = "Sofya Petrova",
        FromEmail = "spam2019@example.com",
        BodyHtml = html,
        BodyText = text,
        To = { new EmailAddress("sales16-meds@examplefreight.com", "Ada") },
        Cc = { new EmailAddress("robot@example.com", "Robot") }
    };

    [Fact]
    public void Build_保留原文HTML排版与表格()
    {
        const string html = """
<table border="1" cellpadding="4"><tbody><tr>
<td><b>运费</b></td><td style="color:#C00000">USD 1200</td>
</tr></tbody></table>
""";

        var result = MailQuoteBuilder.Build(Detail(html));

        Assert.Contains("<table", result);
        Assert.Contains("<b>运费</b>", result);
        Assert.Contains("color:#C00000", result);
        // 纯文本化会把标签变成 &lt;table&gt;：这里必须还是真标签
        Assert.DoesNotContain("&lt;table", result);
    }

    [Fact]
    public void Build_引用块包在mc_quote里且抬头含发件人与主题()
    {
        var result = MailQuoteBuilder.Build(Detail("<p>正文</p>"));

        Assert.StartsWith(MailQuoteBuilder.EmptyLineHtml, result);
        Assert.Contains("class=\"" + MailQuoteBuilder.QuoteClass + "\"", result);
        Assert.Contains(ComposeBodyBuilder.QuoteSeparator, result);
        Assert.Contains("Sofya Petrova &lt;spam2019@example.com&gt;", result);
        Assert.Contains("抄送：", result);
        Assert.Contains("DEMO-REF-0001", result);
        // 正文落在 mc-quote-body 里
        var bodyStart = result.IndexOf(MailQuoteBuilder.BodyClass, StringComparison.Ordinal);
        Assert.True(bodyStart > 0);
        Assert.Contains("正文", result[bodyStart..]);
    }

    [Fact]
    public void Build_内嵌图片由file引用变成data_URI()
    {
        const string html = "<p>看图</p><img src=\"file:///D:/mail/1/logo.png\">";

        var result = MailQuoteBuilder.Build(Detail(html), _ => PngBytes);

        // 标签头必须还在：只换 URI，不能把 <img 一起吃掉（吃掉后 data: 会变成一大段可见的 base64 文字）
        Assert.Contains("<img src=\"data:image/png;base64,", result);
        Assert.DoesNotContain("file:///", result);
        Assert.DoesNotContain("</p>src=", result);
    }

    [Fact]
    public void Build_同一张图片只读一次并复用data_URI()
    {
        const string html = """
<img src="file:///D:/mail/1/logo.png"><img src="file:///D:/mail/1/logo.png">
""";
        var reads = 0;

        var result = MailQuoteBuilder.Build(Detail(html), _ => { reads++; return PngBytes; });

        Assert.Equal(1, reads);
        Assert.Equal(2, result.Split("src=\"data:image/png;base64,").Length - 1);
    }

    [Fact]
    public void Build_超过单图上限的图片不内联()
    {
        const string html = "<img src=\"file:///D:/mail/1/huge.png\">";
        var huge = new byte[MailQuoteBuilder.MaxInlineImageBytes + 1];
        PngBytes.CopyTo(huge, 0);

        var result = MailQuoteBuilder.Build(Detail(html), _ => huge);

        Assert.Contains("file:///D:/mail/1/huge.png", result);
        Assert.DoesNotContain("data:image", result);
    }

    [Fact]
    public void Build_读文件失败不影响其它内容()
    {
        const string html = "<p>正文</p><img src=\"file:///D:/mail/1/gone.png\">";

        var result = MailQuoteBuilder.Build(Detail(html), _ => null);

        Assert.Contains("正文", result);
        Assert.Contains("file:///D:/mail/1/gone.png", result);
    }

    [Fact]
    public void Build_去掉脚本与整篇样式()
    {
        const string html = """
<style>body{background:#000}</style>
<p onclick="alert(1)">正文</p>
<script>alert(1)</script>
""";

        var result = MailQuoteBuilder.Build(Detail(html));

        Assert.DoesNotContain("<script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("background:#000", result);
        Assert.Contains("正文", result);
    }

    [Fact]
    public void Build_没有HTML时按纯文本生成段落()
    {
        var result = MailQuoteBuilder.Build(Detail("", "第一行\n第二行"));

        Assert.Contains("<p>第一行</p>", result);
        Assert.Contains("<p>第二行</p>", result);
    }

    [Fact]
    public void Build_深层引用压平缩进但不折叠()
    {
        const string html = """
<blockquote><div style="margin:10px">一层
<blockquote><div style="margin:10px">二层
<blockquote><div style="margin:10px">三层</div></blockquote>
</blockquote></blockquote>
""";

        var result = MailQuoteBuilder.Build(Detail(html));

        Assert.Contains("q-deep", result);
        Assert.DoesNotContain("quote-fold", result);
        Assert.Contains("三层", result);
    }
}
