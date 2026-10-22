using ZungMail.Core.Domain;

namespace ZungMail.Application.Services;

/// <summary>
/// 规则命中后的流转策略（对齐 Foxmail 过滤器）。
/// 默认语义：标记类动作（星标 / 已读）不改变邮件归属，可继续匹配后续规则；
/// 移动类动作（移动到文件夹 / 归档 / 垃圾邮件）已决定邮件归属，终止后续匹配。
/// 规则勾选「命中后继续执行后续规则」（<see cref="MailRule.ContinueAfterMatch"/>）时，无论动作类型都继续匹配。
/// </summary>
public static class MailRuleFlow
{
    /// <summary>该动作是否会改变邮件归属（移动/归档/垃圾邮件）。</summary>
    public static bool ChangesLocation(MailRuleAction action)
        => action is MailRuleAction.MoveToFolder or MailRuleAction.Archive or MailRuleAction.MarkJunk;

    /// <summary>该规则命中后是否继续评估后续规则。</summary>
    public static bool ShouldContinueAfter(MailRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.ContinueAfterMatch || !ChangesLocation(rule.Action);
    }
}
