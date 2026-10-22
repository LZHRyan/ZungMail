using ZungMail.Core.Domain;

namespace ZungMail.Application.Services;

/// <summary>
/// 规则条件匹配（纯函数，便于单测）。
/// 条件之间是「与」关系：填了哪个就必须命中哪个，全空视为不匹配（避免误伤所有邮件）。
/// </summary>
public static class MailRuleMatch
{
    public static bool Matches(MailRule rule, EmailDetail message)
        => Matches(rule, message.FromName, message.FromEmail, message.Subject);

    public static bool Matches(MailRule rule, string? fromName, string? fromEmail, string? subject)
    {
        if (rule is null || !rule.HasCondition) return false;

        if (!string.IsNullOrWhiteSpace(rule.FromContains))
        {
            var from = $"{fromName} {fromEmail}";
            if (!from.Contains(rule.FromContains, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.FromDomain))
        {
            var domain = DisplayFormat.DomainOf(fromEmail);
            if (!domain.Contains(rule.FromDomain.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.SubjectContains)
            && !(subject ?? string.Empty).Contains(rule.SubjectContains, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
