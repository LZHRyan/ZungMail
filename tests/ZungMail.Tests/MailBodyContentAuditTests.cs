using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>正文渲染对账：只有「明显少了一段」才该报，正常邮件不能吵。</summary>
[Trait("Category", "Unit")]
public sealed class MailBodyContentAuditTests
{
    private static readonly MailDocumentOptions Default = new();

    [Fact]
    public void 正常正文_不判定为丢失()
    {
        const string body =
            "<div>Dear Alex</div><div>still checking</div><hr>" +
            "<blockquote><div>Dear, any news??</div></blockquote>";

        var document = HtmlMailDocumentBuilder.Build(body, Default);
        var audit = MailBodyContentAudit.Inspect(body, document);

        Assert.Equal(0, audit.LostChars);
        Assert.False(audit.Significant);
        Assert.Empty(audit.LostSample);
    }

    [Fact]
    public void 短正文_不判定为丢失()
    {
        // 一句「收到」丢了也能算出很大的百分比，这类噪声不该进日志
        var audit = MailBodyContentAudit.Inspect("<p>收到</p>", "<p>收</p>");

        Assert.False(audit.Significant);
    }

    [Fact]
    public void 开头整段消失_判定为丢失并给出样本()
    {
        var missing = "Dear, any news?? " + new string('m', 200);
        var rest = "<blockquote><div>Dear Alex</div><div>still checking</div>" + new string('x', 200) + "</blockquote>";

        var audit = MailBodyContentAudit.Inspect("<div>" + missing + "</div>" + rest, rest);

        Assert.True(audit.Significant);
        // 样本要指认出被吞掉的那段原文，日志里一眼能看懂丢了什么
        Assert.Contains("any news", audit.LostSample);
    }

    [Fact]
    public void 尾部整段消失_判定为丢失并给出尾部样本()
    {
        var body = "<div>" + new string('a', 200) + "</div><div>" + new string('b', 300) + "</div>";

        var audit = MailBodyContentAudit.Inspect(body, "<div>" + new string('a', 200) + "</div>");

        Assert.True(audit.Significant);
        // 尾部丢失时的样本是被吞掉的尾部原文
        Assert.Equal(120, audit.LostSample.Length);
        Assert.DoesNotContain('a', audit.LostSample);
    }

    [Fact]
    public void 空正文_不抛异常也不误报()
    {
        var audit = MailBodyContentAudit.Inspect(null, null);

        Assert.Equal(0, audit.SourceChars);
        Assert.False(audit.Significant);
    }

    /// <summary>与真实故障同构：脏 HTML 让开头丢掉时，组装器的对账必须能发现。</summary>
    [Fact]
    public void 脏HTML丢开头_对账能发现()
    {
        var source =
            "<html><head></head><div>Dear, any news??</div><div>" + new string('n', 200) + "</div>" +
            "<body><div>" + new string('y', 300) + "</div></body></html>";

        // 模拟「只取 body」的旧行为：开头那两段被吞
        var truncated = "<div>" + new string('y', 300) + "</div>";
        var document = HtmlMailDocumentBuilder.Build(truncated, Default);

        var audit = MailBodyContentAudit.Inspect(source, document);
        Assert.True(audit.Significant);
        Assert.Contains("any news", audit.LostSample);

        // 加固后的组装器不该再丢：对账应当干净
        var fixedDocument = HtmlMailDocumentBuilder.Build(source, Default);
        Assert.False(MailBodyContentAudit.Inspect(source, fixedDocument).Significant);
    }
}
