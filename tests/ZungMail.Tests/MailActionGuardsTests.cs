using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>草稿邮件不能回复 / 回复全部 / 转发：草稿还没发出去，只能继续编辑或删除。</summary>
[Trait("Category", "Unit")]
public sealed class MailActionGuardsTests
{
    [Fact]
    public void 普通邮件_可以回复转发()
    {
        Assert.True(MailActionGuards.CanReplyOrForward(new EmailSummary { Flags = MessageFlags.Seen }));
    }

    [Fact]
    public void 草稿邮件_不能回复转发()
    {
        Assert.False(MailActionGuards.CanReplyOrForward(new EmailSummary { Flags = MessageFlags.Draft }));
    }

    [Fact]
    public void 草稿加已读标志_仍然不能回复转发()
    {
        var draft = new EmailSummary { Flags = MessageFlags.Draft | MessageFlags.Seen };

        Assert.True(draft.IsDraftFlag);
        Assert.False(MailActionGuards.CanReplyOrForward(draft));
    }

    [Fact]
    public void 详情还没加载_用列表行状态判断()
    {
        var row = new EmailSummary { Flags = MessageFlags.Draft };

        Assert.False(MailActionGuards.CanReplyOrForward(null, row));
    }

    [Fact]
    public void 没有选中邮件_不可用()
    {
        Assert.False(MailActionGuards.CanReplyOrForward(null, null));
    }
}
