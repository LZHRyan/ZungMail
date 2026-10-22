using ZungMail.App.ViewModels;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>列表行「陌生」角标：只有陌生邮件显示，且跟随设置开关。</summary>
[Trait("Category", "Unit")]
public class MailRowStrangerBadgeTests
{
    private static MailRow CreateRow(bool isStranger) => new(new EmailSummary
    {
        Id = 1,
        Subject = "账号变更通知",
        FromEmail = "newagent@example.com",
        IsStranger = isStranger
    });

    [Fact]
    public void 陌生邮件显示角标()
    {
        var row = CreateRow(true);

        Assert.True(row.IsStranger);
        Assert.True(row.StrangerBadgeVisible);
        Assert.Contains("首次收到", row.StrangerHint);
    }

    [Fact]
    public void 熟人来信不显示角标()
    {
        var row = CreateRow(false);

        Assert.False(row.IsStranger);
        Assert.False(row.StrangerBadgeVisible);
    }

    [Fact]
    public void 关闭开关后角标消失_重新打开恢复()
    {
        var row = CreateRow(true);

        row.StrangerBadgeEnabled = false;
        Assert.False(row.StrangerBadgeVisible);

        row.StrangerBadgeEnabled = true;
        Assert.True(row.StrangerBadgeVisible);
    }
}
