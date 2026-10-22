using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 账户级收取策略：把「继承 / 完整 / 仅正文 / 仅邮件头」解析成同步真正要用的三个参数。
/// 关键是「继承」必须与升级前的全局行为逐位一致，且「完整收取」不受日期窗口限制。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MailFetchPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 继承默认_跟随全局窗口并下载附件()
    {
        var policy = MailFetchPolicy.Resolve(FetchPolicy.Inherit, 7, Now);

        Assert.True(policy.PrefetchBody);
        Assert.True(policy.SaveAttachments);
        Assert.Equal(Now.AddDays(-7), policy.BodyFloorUtc);
    }

    [Fact]
    public void 继承默认_全局只收邮件头时同样不预取()
    {
        var policy = MailFetchPolicy.Resolve(FetchPolicy.Inherit, 0, Now);

        Assert.False(policy.PrefetchBody);
        Assert.False(policy.SaveAttachments);
        Assert.Null(policy.BodyFloorUtc);
    }

    [Fact]
    public void 继承默认_全局不限日期时下限为最小值()
    {
        var policy = MailFetchPolicy.Resolve(FetchPolicy.Inherit, AppSettings.BodyPrefetchAllDays, Now);

        Assert.True(policy.PrefetchBody);
        Assert.Equal(DateTimeOffset.MinValue, policy.BodyFloorUtc);
    }

    [Fact]
    public void 完整收取_不受全局窗口限制且下载附件()
    {
        var policy = MailFetchPolicy.Resolve(FetchPolicy.Full, 7, Now);

        Assert.True(policy.PrefetchBody);
        Assert.True(policy.SaveAttachments);
        // null 表示查询不限日期（全部邮件都预取），不是「不预取」
        Assert.Null(policy.BodyFloorUtc);
    }

    [Fact]
    public void 仅收取正文_沿用窗口但不落盘附件()
    {
        var policy = MailFetchPolicy.Resolve(FetchPolicy.BodyOnly, 7, Now);

        Assert.True(policy.PrefetchBody);
        Assert.False(policy.SaveAttachments);
        Assert.Equal(Now.AddDays(-7), policy.BodyFloorUtc);
    }

    [Fact]
    public void 仅收取正文_没有窗口时退化为只收邮件头()
    {
        var policy = MailFetchPolicy.Resolve(FetchPolicy.BodyOnly, 0, Now);

        Assert.False(policy.PrefetchBody);
        Assert.False(policy.SaveAttachments);
    }

    [Fact]
    public void 仅收取邮件头_什么都不预取()
    {
        var policy = MailFetchPolicy.Resolve(FetchPolicy.HeadersOnly, 30, Now);

        Assert.False(policy.PrefetchBody);
        Assert.False(policy.SaveAttachments);
        Assert.Null(policy.BodyFloorUtc);
    }

    [Theory]
    [InlineData(FetchPolicy.Inherit, "继承默认策略")]
    [InlineData(FetchPolicy.Full, "完整收取")]
    [InlineData(FetchPolicy.BodyOnly, "仅收取正文")]
    [InlineData(FetchPolicy.HeadersOnly, "仅收取邮件头")]
    public void 下拉文案包含关键区分(FetchPolicy policy, string expected)
        => Assert.Contains(expected, MailFetchPolicy.Label(policy));
}
