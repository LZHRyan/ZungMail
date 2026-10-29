using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>正文 HTML 文档组装：安全策略、URI 重写与主题注入。</summary>
[Trait("Category", "Unit")]
public sealed class HtmlMailDocumentBuilderTests
{
    private static readonly MailDocumentOptions Default = new();

    [Fact]
    public void 空正文_仍生成合法文档()
    {
        var html = HtmlMailDocumentBuilder.Build(new EmailDetail(), Default);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("id=\"mail-root\"", html);
        Assert.Contains("Content-Security-Policy", html);
    }

    [Fact]
    public void 正文统一字体_邮件自带的font_family被强制覆盖()
    {
        var detail = new EmailDetail
        {
            BodyHtml = "<p style=\"font-family:宋体\">正文</p><font face=\"Arial\">另一段</font>"
        };

        var html = HtmlMailDocumentBuilder.Build(detail, Default);

        // 统一字族：即使邮件自带 font-family / <font face>，也要被 !important 压掉
        Assert.Contains("#mail-root,#mail-root *{font-family:" + MailBodyTypography.CssFontStack + " !important;}", html);
        // 代码块保留等宽
        Assert.Contains("font-family:" + MailBodyTypography.CssMonospaceStack + " !important;", html);
        // 只统一字族：字号仍走变量（邮件字号未被替换成固定值）
        Assert.Contains("font-size:var(--font-size)", html);
    }

    [Fact]
    public void 无HTML时_使用纯文本生成段落()
    {
        var detail = new EmailDetail { BodyText = "第一行\n第二行" };

        var html = HtmlMailDocumentBuilder.Build(detail, Default);

        Assert.Contains("<p>第一行</p>", html);
        Assert.Contains("<p>第二行</p>", html);
    }

    [Fact]
    public void 正文滚动条收窄_与桌面端一致()
    {
        var html = HtmlMailDocumentBuilder.Build(new EmailDetail { BodyText = "内容" }, Default);

        Assert.Contains("::-webkit-scrollbar{width:6px", html);
        Assert.Contains("::-webkit-scrollbar-thumb{background:rgba(100,116,139,.45)", html);
    }

    [Fact]
    public void 默认注入CSP并剥离脚本()
    {
        const string body = "<p>你好</p><script>alert(1)</script>";

        var html = HtmlMailDocumentBuilder.Build(body, Default);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("script-src 'none'", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("form-action 'none'", html);
    }

    [Fact]
    public void 剥离内联事件_框架与刷新元标签()
    {
        const string body =
            "<img src=\"https://x/y.png\" onerror=\"alert(1)\">" +
            "<iframe src=\"https://evil\"></iframe>" +
            "<meta http-equiv=\"refresh\" content=\"0;url=https://evil\">" +
            "<a href=\"javascript:alert(2)\">点我</a>";

        var html = HtmlMailDocumentBuilder.Build(body, Default);

        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http-equiv=\"refresh\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"#\"", html);
    }

    [Fact]
    public void 默认阻止远程图片()
    {
        var html = HtmlMailDocumentBuilder.Build("<img src=\"https://track/x.png\">", Default);

        Assert.Contains("img-src https://zungmailassets.example data:;", html);
        Assert.DoesNotContain("data: https:", html);
    }

    [Fact]
    public void 允许远程图片时放宽CSP()
    {
        var options = new MailDocumentOptions { AllowRemoteImages = true };

        var html = HtmlMailDocumentBuilder.Build("<img src=\"https://track/x.png\">", options);

        Assert.Contains("img-src https://zungmailassets.example data: https: http:;", html);
    }

    [Fact]
    public void 本地附件图片重写为虚拟主机()
    {
        var root = Path.Combine(Path.GetTempPath(), "zungmail-tests", "eml");
        var file = Path.Combine(root, "1", "2", "a b.png");
        var body = $"<img src=\"{new Uri(file).AbsoluteUri}\">";

        var html = HtmlMailDocumentBuilder.Build(body, new MailDocumentOptions { EmlDirectory = root });

        Assert.Contains("https://zungmailassets.example/1/2/a%20b.png", html);
        Assert.DoesNotContain("file:///", html);
    }

    [Fact]
    public void 附件文件名含括号时仍重写为虚拟主机()
    {
        var root = Path.Combine(Path.GetTempPath(), "zungmail-tests", "eml");
        var file = Path.Combine(root, "1", "2", "水单(1).png");
        var body = $"<img src=\"{new Uri(file).AbsoluteUri}\">";

        var html = HtmlMailDocumentBuilder.Build(body, new MailDocumentOptions { EmlDirectory = root });

        Assert.Contains("https://zungmailassets.example/1/2/", html);
        Assert.DoesNotContain("file:///", html);
    }

    [Fact]
    public void 附件目录之外的本地文件不改写()
    {
        var body = "<img src=\"file:///C:/Windows/win.ini\">";

        var html = HtmlMailDocumentBuilder.Build(
            body,
            new MailDocumentOptions { EmlDirectory = Path.Combine(Path.GetTempPath(), "zungmail-tests", "eml") });

        Assert.Contains("file:///C:/Windows/win.ini", html);
    }

    [Fact]
    public void 多层引用默认保留最近五层_关闭设置后直接展开()
    {
        var body = BuildNestedQuotes(8);

        var folded = HtmlMailDocumentBuilder.Build(body, Default);
        Assert.Contains("<details", folded);
        Assert.Contains("显示更早的历史邮件（3 层）", folded);

        var expanded = HtmlMailDocumentBuilder.Build(
            body,
            new MailDocumentOptions { FoldQuotedHistory = false });

        Assert.DoesNotContain("<details", expanded);
        Assert.DoesNotContain("class=\"quote-fold\"", expanded);
        // 不折叠也要压平深层缩进，长线程才不会越缩越窄
        Assert.Contains("q-deep", expanded);

        // 保留层数配成 0 时退回「整段收起」
        var whole = HtmlMailDocumentBuilder.Build(
            body,
            new MailDocumentOptions { FoldQuotedHistoryKeepLayers = 0 });
        Assert.Contains("显示更早的历史邮件（8 层）", whole);
    }

    [Fact]
    public void 引用层数不超过保留层数时不折叠()
    {
        var body = BuildNestedQuotes(4);

        var document = HtmlMailDocumentBuilder.Build(body, Default);

        Assert.DoesNotContain("<details", document);
        Assert.DoesNotContain("class=\"quote-fold\"", document);
    }

    private static string BuildNestedQuotes(int layers)
    {
        var builder = new System.Text.StringBuilder("<p>最新的回复</p>");
        for (var i = 1; i <= layers; i++) builder.Append("<blockquote><p>第").Append(i).Append("层引用</p>");
        for (var i = 0; i < layers; i++) builder.Append("</blockquote>");
        return builder.ToString();
    }

    [Fact]
    public void 引用历史里的cid图片_渲染成占位提示()
    {
        // 长线程折叠区展开后是一串破图：这些 cid 属于更早的那封邮件，部件不在本封邮件里
        const string body = "<p>文字<img src=\"cid:logo@x\" width=\"80\" height=\"40\">结束</p>";

        var html = HtmlMailDocumentBuilder.Build(body, Default);

        Assert.DoesNotContain("cid:", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("mc-missing-image", html);
        Assert.Contains("图片未随这封邮件转发", html);
        Assert.Contains("文字", html);
        Assert.Contains("结束", html);
    }

    [Fact]
    public void 可解析的图片_保持原样()
    {
        const string body = "<img src=\"data:image/png;base64,AAAA\"><img src=\"https://x/y.png\">";

        var html = HtmlMailDocumentBuilder.Build(body, Default);

        Assert.Contains("data:image/png;base64,AAAA", html);
        Assert.Contains("https://x/y.png", html);
    }

    [Fact]
    public void 撰写片段的cid图片_直接丢弃不插提示()
    {
        // 撰写区里的占位文字会被一起发出去，所以只删不提示
        const string body = "<p>正文<img src=\"cid:a@b\"></p>";

        var fragment = HtmlMailDocumentBuilder.BuildEditorFragment(body);

        Assert.DoesNotContain("cid:", fragment);
        Assert.DoesNotContain("图片未随这封邮件转发", fragment);
        Assert.Contains("正文", fragment);
    }

    [Fact]
    public void 完整HTML文档_只取body内容()
    {
        const string document =
            "<html><head><title>标题</title></head><body><p>正文内容</p></body></html>";

        var html = HtmlMailDocumentBuilder.Build(document, Default);

        Assert.DoesNotContain("<title>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p>正文内容</p>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<body>"));
    }

    [Fact]
    public void 保留head中的表格边框样式()
    {
        const string document =
            "<html><head><style>table{border-collapse:collapse;width:100%}" +
            "td,th{border:1px solid #ccd}</style></head>" +
            "<body><table><tr><td>a</td></tr></table></body></html>";

        var html = HtmlMailDocumentBuilder.Build(document, Default);

        Assert.Contains("td,th{border:1px solid #ccd}", html);
        Assert.Contains("border-collapse:collapse;width:100%", html);
        // 邮件自带样式必须排在阅读器基础样式之前，保证阅读偏好仍可覆盖字号与配色
        Assert.True(html.IndexOf("td,th{border:1px solid #ccd}", StringComparison.Ordinal) <
                    html.IndexOf("--bg:#FFFFFF", StringComparison.Ordinal));
    }

    [Fact]
    public void head样式中的import被剥离()
    {
        const string document =
            "<html><head><style>@import url(https://evil/a.css);td{border:1px solid #000}</style></head>" +
            "<body><p>x</p></body></html>";

        var html = HtmlMailDocumentBuilder.Build(document, Default);

        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("td{border:1px solid #000}", html);
    }

    [Fact]
    public void 正文样式保留并剥离import()
    {
        const string body =
            "<style>td{border:1px solid #000}@import url(https://evil/a.css);</style>" +
            "<table><tr><td>a</td></tr></table>";

        var html = HtmlMailDocumentBuilder.Build(body, Default);

        Assert.Contains("td{border:1px solid #000}", html);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void border属性表格启用合并边框()
    {
        var html = HtmlMailDocumentBuilder.Build(
            "<table border=\"1\"><tr><td>a</td></tr></table>", Default);

        Assert.Contains(
            ":where(table[border]:not([border=\"0\"]):not([border=\"0px\"])){border-collapse:collapse;}",
            html);
    }

    [Fact]
    public void cellspacing为零的表格启用合并边框()
    {
        var html = HtmlMailDocumentBuilder.Build(
            "<table cellspacing=\"0\"><tr><td>a</td></tr></table>", Default);

        Assert.Contains(":where(table[cellspacing=\"0\"]){border-collapse:collapse;}", html);
    }

    [Fact]
    public void 暗色主题丢弃head配色但保留边框()
    {
        const string document =
            "<html><head><style>td{border:1px solid #ccd;background:#f5f8fc;color:#111}" +
            "th{border-bottom:1px solid #999}</style></head>" +
            "<body><table><tr><th>h</th></tr><tr><td>a</td></tr></table></body></html>";

        var dark = HtmlMailDocumentBuilder.Build(document, new MailDocumentOptions { IsDark = true });

        Assert.Contains("border:1px solid #ccd", dark);
        Assert.Contains("border-bottom:1px solid #999", dark);
        Assert.DoesNotContain("background:#f5f8fc", dark);
        Assert.DoesNotContain("color:#111", dark);

        var light = HtmlMailDocumentBuilder.Build(document, Default);
        Assert.Contains("background:#f5f8fc", light);
        Assert.Contains("color:#111", light);
    }

    [Fact]
    public void 主题与字号写入样式()
    {
        var dark = HtmlMailDocumentBuilder.Build(
            "<p>x</p>",
            new MailDocumentOptions { IsDark = true, FontSize = 15.5 });

        Assert.Contains("--bg:#161B23", dark);
        Assert.Contains("--ink:#E9EEF7", dark);
        Assert.Contains("--font-size:15.5px", dark);

        var light = HtmlMailDocumentBuilder.Build("<p>x</p>", Default);
        Assert.Contains("--bg:#FFFFFF", light);
    }

    [Fact]
    public void 正文始终允许选中复制()
    {
        var html = HtmlMailDocumentBuilder.Build(
            "<p style=\"user-select:none\">不可选中</p>",
            Default);

        // 注入的规则覆盖邮件自带的 user-select:none
        Assert.Contains("#mail-root,#mail-root *{-webkit-user-select:text !important;user-select:text !important;}", html);
    }

    [Theory]
    [InlineData("<img src=\"https://a/b.png\">")]
    [InlineData("<img src='http://a/b.png'>")]
    [InlineData("<img src=\"//cdn.example/b.png\">")]
    [InlineData("<td background=\"https://a/bg.png\">x</td>")]
    [InlineData("<div style=\"background:url(https://a/bg.png)\">x</div>")]
    public void 识别远程图片(string body)
    {
        Assert.True(HtmlMailDocumentBuilder.ContainsRemoteImages(body));
    }

    [Theory]
    [InlineData("<img src=\"file:///C:/a/b.png\">")]
    [InlineData("<img src=\"data:image/png;base64,AAAA\">")]
    [InlineData("<p>纯文本</p>")]
    public void 本地与内嵌图片不算远程(string body)
    {
        Assert.False(HtmlMailDocumentBuilder.ContainsRemoteImages(body));
    }

    /// <summary>
    /// 脏 HTML 的第一类形态：正文写在 &lt;body&gt; 之前。以前按「&lt;body&gt;…&lt;/body&gt;」正则截取，
    /// 开头这段会被整块丢掉 —— 表现就是「对方新写的话不见了，只剩下面的引用历史」。
    /// </summary>
    [Fact]
    public void 正文写在body之前_不丢开头()
    {
        const string document =
            "<html><head><style>p{margin:0}</style></head>" +
            "<div>Dear,<br>any news??</div>" +
            "<body><div>Dear Alex</div><div>still checking</div>" +
            "<hr style=\"width:210px\" color=\"#b5c4df\"></body></html>";

        var html = HtmlMailDocumentBuilder.Build(document, Default);

        Assert.Contains("any news", html);
        Assert.Contains("still checking", html);
        // 顺序也要对：新写的话仍排在引用之前
        Assert.True(html.IndexOf("any news", StringComparison.Ordinal) <
                    html.IndexOf("still checking", StringComparison.Ordinal));
    }

    /// <summary>第二类形态：内容追加在 &lt;/body&gt; 之后（有些客户端把签名或尾注放在这里）。</summary>
    [Fact]
    public void 内容写在body之后_不丢尾部()
    {
        const string document =
            "<html><body><p>正文</p></body><p>尾部签名</p></html>";

        var html = HtmlMailDocumentBuilder.Build(document, Default);

        Assert.Contains("正文", html);
        Assert.Contains("尾部签名", html);
    }

    /// <summary>第三类形态：body 标签没闭合，以前正则会失配后走「剥标签」路径。</summary>
    [Fact]
    public void body标签未闭合_正文仍完整()
    {
        const string document =
            "<html><head><meta charset=\"utf-8\"></head><body><p>第一段</p><p>第二段";

        var html = HtmlMailDocumentBuilder.Build(document, Default);

        Assert.Contains("第一段", html);
        Assert.Contains("第二段", html);
    }
}

