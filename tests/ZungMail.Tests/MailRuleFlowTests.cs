using ZungMail.Application.Services;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.Tests;

/// <summary>规则命中后的流转策略：默认按动作类型决定，勾选「继续执行」后一律继续。</summary>
public class MailRuleFlowTests
{
    [Theory]
    [InlineData(MailRuleAction.Star)]
    [InlineData(MailRuleAction.MarkSeen)]
    public void 标记类动作不改变归属且默认继续匹配(MailRuleAction action)
    {
        var rule = new MailRule { Action = action };

        Assert.False(MailRuleFlow.ChangesLocation(action));
        Assert.True(MailRuleFlow.ShouldContinueAfter(rule));
    }

    [Theory]
    [InlineData(MailRuleAction.MoveToFolder)]
    [InlineData(MailRuleAction.Archive)]
    [InlineData(MailRuleAction.MarkJunk)]
    public void 移动类动作默认终止后续规则(MailRuleAction action)
    {
        var rule = new MailRule { Action = action };

        Assert.True(MailRuleFlow.ChangesLocation(action));
        Assert.False(MailRuleFlow.ShouldContinueAfter(rule));
    }

    [Theory]
    [InlineData(MailRuleAction.MoveToFolder)]
    [InlineData(MailRuleAction.Archive)]
    [InlineData(MailRuleAction.MarkJunk)]
    [InlineData(MailRuleAction.Star)]
    public void 勾选继续执行后一律继续匹配(MailRuleAction action)
    {
        var rule = new MailRule { Action = action, ContinueAfterMatch = true };

        Assert.True(MailRuleFlow.ShouldContinueAfter(rule));
    }

    [Fact]
    public void 空规则抛出参数异常()
        => Assert.Throws<ArgumentNullException>(() => MailRuleFlow.ShouldContinueAfter(null!));
}
