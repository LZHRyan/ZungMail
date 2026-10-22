using ZungMail.App.ViewModels;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>列表行「疑似钓鱼」角标：只有命中仿冒域名时出现，提示里带基准域名。</summary>
[Trait("Category", "Unit")]
public class MailRowPhishingBadgeTests
{
    private static MailRow CreateRow() => new(new EmailSummary
    {
        Id = 1,
        Subject = "变更收款账号通知",
        FromEmail = "finance@examplefreightt.com"
    });

    [Fact]
    public void 命中仿冒域名时显示角标与说明()
    {
        var row = CreateRow();
        Assert.False(row.PhishingBadgeVisible);

        row.Phishing = new PhishingDomainVerdict(
            "examplefreight.com", "examplefreightt.com", PhishingDomainReason.Lookalike);

        Assert.True(row.PhishingBadgeVisible);
        Assert.Contains("examplefreight.com", row.PhishingHint);
    }

    [Fact]
    public void 没有命中时不显示()
    {
        var row = CreateRow();

        Assert.False(row.PhishingBadgeVisible);
        Assert.Equal("", row.PhishingHint);
    }

    [Fact]
    public void 结论清空后角标消失()
    {
        var row = CreateRow();
        row.Phishing = new PhishingDomainVerdict(
            "examplefreight.com", "examplefreight.cn", PhishingDomainReason.SuffixSwap);
        Assert.True(row.PhishingBadgeVisible);

        row.Phishing = null;
        Assert.False(row.PhishingBadgeVisible);
    }
}
