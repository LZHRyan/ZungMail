using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 发送前检查：漏附件、空主题、收件人过多这类问题只提示不阻断。
/// 判定逻辑放在 Core，界面只负责弹确认框。
/// </summary>
[Trait("Category", "Unit")]
public sealed class ComposeSendGuardTests
{
    private static ComposeCheckInput Input(
        string subject = "订舱确认",
        string body = "<p>正文</p>",
        int attachments = 1,
        int recipients = 1)
        => new(subject, body, attachments, recipients);

    [Fact]
    public void 正常邮件没有任何提示()
        => Assert.Empty(ComposeSendGuard.Check(Input()));

    [Fact]
    public void 主题为空时提示()
    {
        var warnings = ComposeSendGuard.Check(Input(subject: "  "));

        Assert.Contains(warnings, w => w.Code == "empty-subject");
    }

    [Fact]
    public void 正文空时提示()
    {
        var warnings = ComposeSendGuard.Check(Input(body: "<p>&nbsp;</p>"));

        Assert.Contains(warnings, w => w.Code == "empty-body");
    }

    [Theory]
    [InlineData("<p>详见附件。</p>")]
    [InlineData("<p>请查收附上的报价单</p>")]
    [InlineData("<p>please see the attachment</p>")]
    [InlineData("<p>Please find enclosed B/L draft</p>")]
    public void 正文提到附件但没加附件时提示(string body)
    {
        var warnings = ComposeSendGuard.Check(Input(body: body, attachments: 0));

        Assert.Contains(warnings, w => w.Code == "missing-attachment");
    }

    [Fact]
    public void 已经加了附件就不再提示()
    {
        var warnings = ComposeSendGuard.Check(Input(body: "<p>详见附件。</p>", attachments: 2));

        Assert.DoesNotContain(warnings, w => w.Code == "missing-attachment");
    }

    [Fact]
    public void 只有HTML属性里出现附件不算提到附件()
    {
        // 提示词在标签属性里（如 alt="附件"）时不应当成「正文提到附件」
        var warnings = ComposeSendGuard.Check(Input(body: "<p><img alt=\"附件\" src=\"cid:x\"/></p>", attachments: 0));

        Assert.DoesNotContain(warnings, w => w.Code == "missing-attachment");
    }

    [Fact]
    public void 收件人过多时提示群发风险()
    {
        var warnings = ComposeSendGuard.Check(Input(recipients: 25));

        Assert.Contains(warnings, w => w.Code == "many-recipients");
    }

    [Fact]
    public void 收件人20个以内不提示()
    {
        var warnings = ComposeSendGuard.Check(Input(recipients: 20));

        Assert.DoesNotContain(warnings, w => w.Code == "many-recipients");
    }
}
