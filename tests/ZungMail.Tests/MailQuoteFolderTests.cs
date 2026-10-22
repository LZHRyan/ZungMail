using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 长线程正文的引用处理：深层引用压平缩进、最外层历史引用折叠、图片懒加载。
/// 关键点是「只有真的存在多层引用才改写」，普通邮件必须保持原样。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MailQuoteFolderTests
{
    private const string NestedQuotes = """
<p>最新的回复</p>
<blockquote><p>引用一层</p>
<blockquote><p>引用二层</p>
<blockquote><p>引用三层</p>
<blockquote><p>引用四层 <img src="cid:logo.png" /></p></blockquote>
</blockquote>
</blockquote>
</blockquote>
""";

    [Theory]
    [InlineData("")]
    [InlineData("<p>没有引用</p>")]
    [InlineData("<blockquote><p>只有一层</p></blockquote>")]
    public void NeedsRewrite_两层以上引用才需要改写(string html)
        => Assert.False(MailQuoteFolder.NeedsRewrite(html));

    [Fact]
    public void NeedsRewrite_两层引用返回真()
        => Assert.True(MailQuoteFolder.NeedsRewrite(
            "<blockquote><blockquote>两层</blockquote></blockquote>"));

    [Fact]
    public void Rewrite_单层引用原样返回()
    {
        const string html = "<p>正文</p><blockquote><p>引用</p></blockquote>";

        var result = MailQuoteFolder.Rewrite(html);

        Assert.Equal(html, result.Html);
        Assert.False(result.Folded);
        Assert.Equal(0, result.MaxDepth);
    }

    [Fact]
    public void Rewrite_深层引用不加缩进只加标记()
    {
        var result = MailQuoteFolder.Rewrite(NestedQuotes);

        Assert.Equal(4, result.MaxDepth);
        Assert.Contains("q-deep", result.Html);
    }

    [Fact]
    public void Rewrite_整段收起_最外层引用被折叠且带层数文案()
    {
        // keepVisibleLayers = 0 表示不保留可见层，整段历史一次性收起（旧行为）
        var result = MailQuoteFolder.Rewrite(NestedQuotes, keepVisibleLayers: 0);

        Assert.True(result.Folded);
        Assert.Contains("<details class=\"quote-fold\">", result.Html);
        Assert.Contains("显示更早的历史邮件（4 层）", result.Html);
        // 最新的正文留在折叠块外面（在 details 之前）
        Assert.True(
            result.Html.IndexOf("最新的回复", StringComparison.Ordinal)
            < result.Html.IndexOf("<details", StringComparison.Ordinal));
    }

    [Fact]
    public void Rewrite_默认保留最近五层_只收起更早的()
    {
        var html = BuildNestedQuotes(8);

        var result = MailQuoteFolder.Rewrite(html);

        Assert.True(result.Folded);
        Assert.Equal(8, result.MaxDepth);
        Assert.Contains("显示更早的历史邮件（3 层）", result.Html);

        var detailsIndex = result.Html.IndexOf("<details", StringComparison.Ordinal);
        Assert.True(detailsIndex > 0);
        // 最近 5 层仍在折叠块外面，第 6 层起才被收起
        for (var layer = 1; layer <= 5; layer++)
        {
            var index = result.Html.IndexOf($"第{layer}层引用", StringComparison.Ordinal);
            Assert.True(index >= 0 && index < detailsIndex, $"第 {layer} 层应保持可见");
        }

        var sixth = result.Html.IndexOf("第6层引用", StringComparison.Ordinal);
        Assert.True(sixth > detailsIndex, "第 6 层应该被折进 details");
    }

    [Fact]
    public void Rewrite_保留层数不少于总层数时整篇展开()
    {
        var result = MailQuoteFolder.Rewrite(BuildNestedQuotes(6), keepVisibleLayers: 6);

        Assert.False(result.Folded);
        Assert.DoesNotContain("quote-fold", result.Html);
    }

    /// <summary>生成 N 层嵌套引用，供折叠阈值测试使用。</summary>
    private static string BuildNestedQuotes(int layers)
    {
        var builder = new System.Text.StringBuilder("<p>最新的回复</p>");
        for (var i = 1; i <= layers; i++) builder.Append("<blockquote><p>第").Append(i).Append("层引用</p>");
        for (var i = 0; i < layers; i++) builder.Append("</blockquote>");
        return builder.ToString();
    }

    [Fact]
    public void Rewrite_引用里的图片补懒加载属性()
    {
        var result = MailQuoteFolder.Rewrite(NestedQuotes);

        Assert.Contains("loading=\"lazy\"", result.Html);
        Assert.Contains("decoding=\"async\"", result.Html);
    }

    [Fact]
    public void Rewrite_已有的懒加载属性不重复写()
    {
        var html = """
<blockquote><blockquote>
<img src="cid:a.png" loading="eager" decoding="sync" />
</blockquote></blockquote>
""";

        var result = MailQuoteFolder.Rewrite(html);

        Assert.Contains("loading=\"eager\"", result.Html);
        Assert.DoesNotContain("loading=\"lazy\"", result.Html);
    }

    [Fact]
    public void Rewrite_未闭合引用不抛异常()
    {
        var result = MailQuoteFolder.Rewrite(
            "<blockquote><p>第一层<blockquote><p>第二层<blockquote><p>第三层没闭合");

        Assert.False(string.IsNullOrEmpty(result.Html));
        Assert.Contains("第三层没闭合", result.Html);
    }

    [Fact]
    public void Rewrite_两层引用不折叠()
    {
        var result = MailQuoteFolder.Rewrite(
            "<p>回复</p><blockquote><p>一层</p><blockquote><p>两层</p></blockquote></blockquote>");

        Assert.False(result.Folded);
        Assert.Equal(2, result.MaxDepth);
        Assert.DoesNotContain("quote-fold", result.Html);
    }

    [Fact]
    public void Rewrite_深层引用的套娃容器一起压平()
    {
        // 真实长线程（Foxmail/Outlook/网页邮箱）每层引用外面还套着带 margin/padding 的 div，
        // 只压 blockquote 时 39 层仍会累积上千像素缩进，这里连同这些容器一起打标记
        const string html = """
<p>最新回复</p>
<blockquote><div style="margin:10px">一层</div>
<blockquote><div style="margin:10px">二层</div>
<blockquote><div style="margin:10px;padding-left:20px">三层</div>
<blockquote><div style="margin:10px">四层</div></blockquote>
</blockquote>
</blockquote>
</blockquote>
""";

        var result = MailQuoteFolder.Rewrite(html);
        var document = new HtmlParser().ParseDocument(result.Html);

        var flattened = document.QuerySelectorAll(".q-deep").OfType<IHtmlElement>().ToList();
        Assert.Contains(flattened, e => e.LocalName == "blockquote");
        Assert.Contains(flattened, e => e.LocalName == "div" && e.TextContent.Trim() == "三层");
        Assert.Contains(flattened, e => e.LocalName == "div" && e.TextContent.Trim() == "四层");
        // 前两层保持原样，层级感还在
        Assert.DoesNotContain(flattened, e => e.LocalName == "div" && e.TextContent.Trim() == "一层");
        Assert.DoesNotContain(flattened, e => e.LocalName == "div" && e.TextContent.Trim() == "二层");
    }

    [Fact]
    public void Rewrite_表格与行内元素不压平()
    {
        // 报价表靠 cellpadding/border 画网格，清零会把内容挤在一起并丢掉分隔线
        const string html = """
<blockquote><blockquote><blockquote>
<table border="1" cellpadding="6"><tbody><tr><td style="padding-left:12px">运费</td></tr></tbody></table>
<p><span style="margin-left:20px">行内缩进</span></p>
</blockquote></blockquote></blockquote>
""";

        var result = MailQuoteFolder.Rewrite(html);
        var document = new HtmlParser().ParseDocument(result.Html);

        foreach (var name in new[] { "table", "tbody", "tr", "td", "span" })
            Assert.DoesNotContain(document.QuerySelectorAll(".q-deep").OfType<IHtmlElement>(), e => e.LocalName == name);
    }

    [Fact]
    public void Rewrite_没有样式的普通容器不加标记以控制体积()
    {
        const string html = """
<blockquote><blockquote><blockquote>
<div><div><div><p>普通嵌套</p></div></div></div>
</blockquote></blockquote></blockquote>
""";

        var result = MailQuoteFolder.Rewrite(html);
        var document = new HtmlParser().ParseDocument(result.Html);

        // 只有 blockquote 带标记：没有 style/class 的 div 本身不产生额外缩进，不必写 class
        var marked = document.QuerySelectorAll(".q-deep").OfType<IHtmlElement>().ToList();
        Assert.NotEmpty(marked);
        Assert.All(marked, e => Assert.Equal("blockquote", e.LocalName));
        Assert.Contains("普通嵌套", result.Html);
    }

    [Fact]
    public void Rewrite_不要折叠时仍压平缩进()
    {
        // 回复 / 转发的引用原文要能看到全部历史，只压平缩进、不折进 <details>
        var result = MailQuoteFolder.Rewrite(NestedQuotes, fold: false);

        Assert.False(result.Folded);
        Assert.DoesNotContain("quote-fold", result.Html);
        Assert.Contains("q-deep", result.Html);
    }

    [Fact]
    public void 阅读器样式包含深层引用与折叠规则()
    {
        var document = HtmlMailDocumentBuilder.Build(
            "<p>正文</p>",
            new MailDocumentOptions { IsDark = false });

        Assert.Contains("#mail-root .q-deep", document);
        Assert.Contains("details.quote-fold", document);
        Assert.Contains("content-visibility:auto", document);
    }
}
