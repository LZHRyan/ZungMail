using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>摘要输入清洗：去引用、去签名、HTML 兜底、截断。这些规则直接决定摘要质量。</summary>
public sealed class MailSummaryInputTests
{
    [Fact]
    public void 去掉引用只保留最新一层()
    {
        var body = "请安排订舱，SO-12345。\n\n-----Original Message-----\n老消息：上个月的价格。";

        var input = MailSummaryInputBuilder.Build(body, null);

        Assert.Contains("请安排订舱", input.Text);
        Assert.DoesNotContain("上个月的价格", input.Text);
        Assert.False(input.Truncated);
    }

    [Fact]
    public void 去掉尖括号引用行()
    {
        var body = "确认收到。\n> 之前的内容\n> 更多内容";

        var input = MailSummaryInputBuilder.Build(body, null);

        Assert.Equal("确认收到。", input.Text.Trim());
    }

    [Fact]
    public void 去掉尾部签名()
    {
        var body = "请查收附件的报关资料，本周五前要提交。\n\n此致\n张三\n销售经理\n13800000000";

        var input = MailSummaryInputBuilder.Build(body, null);

        Assert.Contains("报关资料", input.Text);
        Assert.DoesNotContain("销售经理", input.Text);
    }

    [Fact]
    public void 纯文本为空时从HTML转换()
    {
        var input = MailSummaryInputBuilder.Build("", "<p>船名航次：<b>HXSCNSVRA260959</b></p>");

        Assert.Contains("HXSCNSVRA260959", input.Text);
        Assert.DoesNotContain("<p>", input.Text);
    }

    [Fact]
    public void 超过上限时截断并标记()
    {
        var body = new string('A', 9000);

        var input = MailSummaryInputBuilder.Build(body, null, maxChars: 3000);

        Assert.Equal(3000, input.Text.Length);
        Assert.True(input.Truncated);
        Assert.Equal(9000, input.OriginalChars);
    }

    [Fact]
    public void 上限夹取到允许区间()
    {
        Assert.Equal(2000, ZungMail.Core.Domain.MailSummarySettings.EffectiveInputChars(100));
        Assert.Equal(12000, ZungMail.Core.Domain.MailSummarySettings.EffectiveInputChars(999999));
    }

    [Fact]
    public void 空正文返回空输入()
    {
        var input = MailSummaryInputBuilder.Build("   ", "<div></div>");

        Assert.False(input.HasContent);
        Assert.Equal(0, input.Text.Length);
    }

    [Fact]
    public void 英文OnWrote引用头也能识别()
    {
        var body = "Please confirm the booking.\nOn 2026-10-20 09:01, Ada <ada@example.com> wrote:\nold thread";

        var input = MailSummaryInputBuilder.Build(body, null);

        Assert.Contains("confirm the booking", input.Text);
        Assert.DoesNotContain("old thread", input.Text);
    }
}
