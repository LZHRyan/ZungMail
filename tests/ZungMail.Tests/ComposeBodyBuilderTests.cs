using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>撰写正文纯逻辑：纯文本 / HTML 判定、签名追加、内嵌图片转 cid。</summary>
[Trait("Category", "Unit")]
public sealed class ComposeBodyBuilderTests
{
    /// <summary>1×1 透明 PNG。</summary>
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";

    // ---------------- 编辑器内容转换 ----------------

    [Fact]
    public void ToEditorHtml_纯文本按行转段落()
        => Assert.Equal("<p>第一行</p><p>第二行</p>", ComposeBodyBuilder.ToEditorHtml("第一行\n第二行"));

    [Fact]
    public void ToEditorHtml_已有HTML原样返回()
    {
        const string html = "<p><strong>hi</strong></p>";
        Assert.Equal(html, ComposeBodyBuilder.ToEditorHtml(html));
    }

    [Fact]
    public void ToEditorHtml_空内容返回空串()
        => Assert.Equal("", ComposeBodyBuilder.ToEditorHtml(null));

    [Fact]
    public void ToEditorHtml_回复引用原文包进mc_quote容器()
    {
        var body = "\n\n" + ComposeBodyBuilder.QuoteSeparator + "\n发件人：ops@forwarder.cn\n正文";

        var html = ComposeBodyBuilder.ToEditorHtml(body);

        Assert.StartsWith("<div class=\"mc-quote\">", html);
        Assert.EndsWith("</div>", html);
        Assert.Contains("发件人：ops@forwarder.cn", html);
    }

    [Fact]
    public void ToEditorHtml_分隔线之前的新写内容留在引用之外()
    {
        var body = "请确认附件。\n\n" + ComposeBodyBuilder.QuoteSeparator + "\n引用正文";

        var html = ComposeBodyBuilder.ToEditorHtml(body);

        var quoteStart = html.IndexOf("<div class=\"mc-quote\">", StringComparison.Ordinal);
        Assert.True(quoteStart > 0);
        Assert.Contains("请确认附件。", html[..quoteStart]);
        Assert.DoesNotContain("引用正文", html[..quoteStart]);
    }

    [Theory]
    [InlineData("张三 <a@b.com>", false)]
    [InlineData("a < b 且 b > c", false)]
    [InlineData("<p>正文</p>", true)]
    [InlineData("<img src=\"cid:x\"/>", true)]
    [InlineData("<br/>", true)]
    public void LooksLikeHtml_只识别真实标签(string text, bool expected)
        => Assert.Equal(expected, ComposeBodyBuilder.LooksLikeHtml(text));

    // ---------------- 签名 ----------------

    [Fact]
    public void AppendSignature_追加纯文本签名并保留原正文()
    {
        var result = ComposeBodyBuilder.AppendSignature("<p>正文</p>", "张三\n市场部");

        Assert.StartsWith("<p>正文</p>", result);
        Assert.Contains("--<br/>张三<br/>市场部", result);
    }

    [Fact]
    public void AppendSignature_正文已含签名时不重复()
    {
        const string body = "<p>正文</p><p>张三</p>";
        Assert.Equal(body, ComposeBodyBuilder.AppendSignature(body, "张三"));
    }

    [Fact]
    public void AppendSignature_空签名原样返回()
        => Assert.Equal("<p>正文</p>", ComposeBodyBuilder.AppendSignature("<p>正文</p>", "  "));

    // ---------------- 富文本签名 ----------------

    [Fact]
    public void AppendSignatureHtml_保留签名格式并追加在正文后()
    {
        const string signature = "<p><strong>张三</strong>｜<span style=\"color:#2563EB\">市场部</span></p>";

        var result = ComposeBodyBuilder.AppendSignatureHtml("<p>正文</p>", signature);

        Assert.StartsWith("<p>正文</p><br/><br/>", result);
        Assert.Contains("<strong>张三</strong>", result);
        Assert.Contains("color:#2563EB", result);
    }

    [Fact]
    public void AppendSignatureHtml_回复正文把签名插在引用之前()
    {
        const string signature = "<p>落款</p>";
        const string body = "<p>请确认</p><div class=\"mc-quote\"><p>原始邮件</p></div>";

        var result = ComposeBodyBuilder.AppendSignatureHtml(body, signature);

        var signatureAt = result.IndexOf("<p>落款</p>", StringComparison.Ordinal);
        var quoteAt = result.IndexOf("<div class=\"mc-quote\">", StringComparison.Ordinal);
        Assert.True(signatureAt > 0);
        Assert.True(quoteAt > signatureAt, "签名必须排在引用块之前");
    }

    [Fact]
    public void AppendSignatureHtml_正文已含同一签名时不重复()
    {
        const string body = "<p>正文</p><p><strong>张三</strong></p>";

        Assert.Equal(body, ComposeBodyBuilder.AppendSignatureHtml(
            body, "<p><strong>张三</strong></p>"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<p>&nbsp;</p>")]
    [InlineData("<p><br/></p>")]
    public void AppendSignatureHtml_无有效内容的签名不追加(string signature)
        => Assert.Equal("<p>正文</p>", ComposeBodyBuilder.AppendSignatureHtml("<p>正文</p>", signature));

    [Fact]
    public void AppendSignatureHtml_空正文时只输出签名()
    {
        var result = ComposeBodyBuilder.AppendSignatureHtml("", "<p><em>落款</em></p>");

        Assert.Equal("<br/><br/><p><em>落款</em></p>", result);
    }

    [Fact]
    public void AppendSignatureHtml_纯图片签名按图片地址去重()
    {
        var signature = $"<p><img src=\"data:image/png;base64,{PngBase64}\"/></p>";
        var bodyWithSignature = $"<p>正文</p>{signature}";

        // 正文里已带同一张签名图：不再追加
        Assert.Equal(bodyWithSignature, ComposeBodyBuilder.AppendSignatureHtml(bodyWithSignature, signature));

        // 正文里没有：追加一次（整体只有签名这一张图）
        var result = ComposeBodyBuilder.AppendSignatureHtml("<p>正文</p>", signature);
        Assert.NotEqual("<p>正文</p>", result);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "<img"));
    }

    // ---------------- 内嵌图片 ----------------

    [Fact]
    public void ExtractInlineImages_把data图片改写为cid并返回资源()
    {
        var html = $"<p><img src=\"data:image/png;base64,{PngBase64}\"></p>";

        var (result, images) = ComposeBodyBuilder.ExtractInlineImages(html);

        var image = Assert.Single(images);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("image.png", image.FileName);
        Assert.True(image.Data.Length > 0);
        Assert.Contains($"src=\"cid:{image.ContentId}\"", result);
        Assert.DoesNotContain("data:image", result);
    }

    [Fact]
    public void ExtractInlineImages_相同图片只生成一个资源并复用cid()
    {
        var uri = $"data:image/png;base64,{PngBase64}";
        var html = $"<img src=\"{uri}\"><img src=\"{uri}\">";

        var (result, images) = ComposeBodyBuilder.ExtractInlineImages(html);

        var image = Assert.Single(images);
        Assert.Equal(2, CountOccurrences(result, $"cid:{image.ContentId}"));
    }

    [Fact]
    public void ExtractInlineImages_单引号属性保持引号风格()
    {
        var html = $"<img src='data:image/jpeg;base64,{PngBase64}'>";

        var (result, images) = ComposeBodyBuilder.ExtractInlineImages(html);

        var image = Assert.Single(images);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Contains($"src='cid:{image.ContentId}'", result);
    }

    [Fact]
    public void ExtractInlineImages_非法base64保持原样()
    {
        const string html = "<img src=\"data:image/png;base64,@@@@\">";

        var (result, images) = ComposeBodyBuilder.ExtractInlineImages(html);

        Assert.Empty(images);
        Assert.Equal(html, result);
    }

    [Fact]
    public void ExtractInlineImages_非图片dataURI不处理()
    {
        const string html = "<a href=\"data:text/plain;base64,SGk=\">链接</a>";

        var (result, images) = ComposeBodyBuilder.ExtractInlineImages(html);

        Assert.Empty(images);
        Assert.Equal(html, result);
    }

    [Fact]
    public void ExtractInlineImages_无内嵌图片时快速返回()
    {
        const string html = "<p>纯正文</p>";

        var (result, images) = ComposeBodyBuilder.ExtractInlineImages(html);

        Assert.Empty(images);
        Assert.Equal(html, result);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
