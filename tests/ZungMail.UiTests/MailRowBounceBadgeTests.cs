using ZungMail.App.ViewModels;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>列表「退信」角标：退信邮件才显示，跟随设置开关立即隐藏/恢复。</summary>
public sealed class MailRowBounceBadgeTests
{
    private static MailRow CreateRow(bool isBounce, string recipient = "", string status = "", string detail = "") =>
        new(new EmailSummary
        {
            Id = 1,
            Subject = "Undelivered Mail Returned to Sender",
            FromEmail = "mailer-daemon@example.com",
            FromName = "Mail Delivery System",
            IsBounce = isBounce,
            BounceRecipient = recipient,
            BounceStatus = status,
            BounceDetail = detail
        });

    [Fact]
    public void 退信邮件显示角标()
    {
        var row = CreateRow(true, "ops@forwarder.cn", "5.1.1", "550 User unknown");

        Assert.True(row.IsBounce);
        Assert.True(row.BounceBadgeVisible);
        Assert.Contains("ops@forwarder.cn", row.BounceHint);
        Assert.Contains("5.1.1", row.BounceHint);
    }

    [Fact]
    public void 普通邮件没有角标()
    {
        var row = CreateRow(false);

        Assert.False(row.IsBounce);
        Assert.False(row.BounceBadgeVisible);
    }

    [Fact]
    public void 关闭设置开关角标立即消失()
    {
        var row = CreateRow(true, "ops@forwarder.cn");
        Assert.True(row.BounceBadgeVisible);

        row.BounceBadgeEnabled = false;

        Assert.False(row.BounceBadgeVisible);
        // 数据仍在（重新开启立刻恢复）
        Assert.True(row.IsBounce);
        row.BounceBadgeEnabled = true;
        Assert.True(row.BounceBadgeVisible);
    }

    [Fact]
    public void 没有细节时提示引导看正文()
    {
        var row = CreateRow(true);

        Assert.Equal("展开正文查看失败原因", row.Model.BounceDetailLine);
        Assert.Contains("没有送达", row.BounceHint);
    }
}
