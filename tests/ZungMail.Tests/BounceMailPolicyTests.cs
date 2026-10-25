using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 退信判定：DSN 结构最可靠；结构缺失时靠「退信类发件人 + 退信主题」双条件，
/// 只有主题或只有发件人都不算，延迟警告一律不算。
/// </summary>
[Trait("Category", "Unit")]
public sealed class BounceMailPolicyTests
{
    [Theory]
    [InlineData("MAILER-DAEMON@example.com")]
    [InlineData("postmaster@qq.com")]
    [InlineData("Mail-Daemon@corp.cn")]
    [InlineData("postfix@relay.local")]
    public void 退信类发件人(string address) => Assert.True(BounceMailPolicy.IsBounceSender(address));

    [Theory]
    [InlineData("ops@forwarder.cn")]
    [InlineData("sales@example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void 普通发件人(string? address) => Assert.False(BounceMailPolicy.IsBounceSender(address));

    [Fact]
    public void 发件人没有地址时看显示名()
    {
        Assert.True(BounceMailPolicy.IsBounceSender("", "Mailer-Daemon"));
    }

    [Theory]
    [InlineData("Undelivered Mail Returned to Sender")]
    [InlineData("Delivery Status Notification (Failure)")]
    [InlineData("Mail delivery failed: returning message to sender")]
    [InlineData("退信：无法投递的邮件")]
    [InlineData("系统退信")]
    [InlineData("邮件投递失败通知")]
    [InlineData("Не удается доставить: Re: DG// 003575853")]
    [InlineData("来自luxitool.com的退信")]
    public void 退信主题(string subject) => Assert.True(BounceMailPolicy.MatchesBounceSubject(subject));

    [Theory]
    [InlineData("Delivery Status Notification (Delay)")]
    [InlineData("Delivery delayed: still trying")]
    [InlineData("退信：延迟投递提醒")]
    public void 延迟提醒不算退信主题(string subject) => Assert.True(BounceMailPolicy.IsDelayNotice(subject));

    [Fact]
    public void DSN结构直接判定为退信()
    {
        Assert.True(BounceMailPolicy.IsBounce("mailer-daemon@example.com", "", "随便什么主题", isDsnReport: true));
    }

    [Fact]
    public void DSN结构但主题是延迟提醒_不算退信()
    {
        Assert.False(BounceMailPolicy.IsBounce(
            "mailer-daemon@example.com", "", "Delivery Status Notification (Delay)", isDsnReport: true));
    }

    [Fact]
    public void 结构缺失时双条件同时成立才算()
    {
        Assert.True(BounceMailPolicy.IsBounce("postmaster@qq.com", "", "退信：无法投递"));
        // 只有主题：客户自己写「退信处理」不算
        Assert.False(BounceMailPolicy.IsBounce("ops@forwarder.cn", "", "关于退信的处理"));
        // 只有发件人：postmaster 发来的正常邮件不算
        Assert.False(BounceMailPolicy.IsBounce("postmaster@partner.com", "", "通讯录更新"));
    }

    [Fact]
    public void 空数据不抛异常()
    {
        Assert.False(BounceMailPolicy.IsBounce(null, null, null));
        Assert.False(BounceMailPolicy.IsBounce("", "", ""));
        Assert.Equal("", BounceMailPolicy.LocalPart(null));
    }

    [Fact]
    public void 大小写与前后空白不影响判定()
    {
        Assert.True(BounceMailPolicy.IsBounce("  MAILER-DAEMON@Example.COM ", "", "  UNDELIVERED MAIL  "));
    }
}
