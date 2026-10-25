namespace ZungMail.Core.Services;

/// <summary>
/// 退信（投递失败退回 / DSN）判定（纯逻辑，便于单测）。
/// <para>
/// 判定只用同步时已经拿到的信息：BODYSTRUCTURE 是否为 <c>multipart/report</c>、
/// 发件人地址、主题。结构信息缺失（服务商 BODYSTRUCTURE 非法已降级）时退化为
/// 「退信类发件人 + 退信主题」双条件，避免把客户写的「关于退信的处理」误标。
/// </para>
/// </summary>
public static class BounceMailPolicy
{
    /// <summary>退信系统常用本地部分（小写比较）。</summary>
    private static readonly string[] BounceLocalParts =
    {
        "mailer-daemon", "mailerdaemon", "mail-daemon", "maildaemon",
        "postmaster", "postmaster-", "mailer", "postfix"
    };

    /// <summary>投递失败主题关键词（小写比较）：英文与国内服务商的中文文案。</summary>
    private static readonly string[] BounceSubjectKeywords =
    {
        "undelivered", "undeliverable", "delivery status notification (failure)", "delivery failure",
        "delivery has failed", "mail delivery failed", "returned to sender", "return to sender",
        "failure notice", "message not delivered", "couldn't be delivered", "could not be delivered",
        "退信", "退件", "无法投递", "投递失败", "邮件退回", "被退回", "发送失败", "无法送达", "不能发送", "未送达",
        // 俄语：本机真实收到的 Fesco / 俄线代理退信就是俄语主题（Не удается доставить = 无法投递）
        "не удается доставить", "не удалось доставить", "недоставленное сообщение", "сообщение не доставлено",
        "ошибка доставки", "возврат сообщения"
    };

    /// <summary>延迟警告关键词：这是「还在重试」不是「投递失败」，不标记为退信。</summary>
    private static readonly string[] DelaySubjectKeywords =
    {
        "delivery status notification (delay)", "delayed delivery", "delivery delayed", "delay", "delayed",
        "will retry", "still trying", "延迟", "延期", "稍后重试", "正在重试"
    };

    /// <summary>取地址的本地部分（小写、去空白）；没有 @ 时整串视为本地部分。</summary>
    public static string LocalPart(string? address)
    {
        var normalized = (address ?? string.Empty).Trim().ToLowerInvariant();
        var at = normalized.IndexOf('@');
        return at > 0 ? normalized[..at] : normalized;
    }

    /// <summary>发件人是否像退信系统（mailer-daemon / postmaster 等）。</summary>
    /// <param name="fromName">邮件头没有地址时（如裸 <c>MAILER-DAEMON</c>）退化为看显示名。</param>
    public static bool IsBounceSender(string? fromEmail, string? fromName = null)
    {
        var local = LocalPart(fromEmail);
        if (local.Length == 0) local = (fromName ?? string.Empty).Trim().ToLowerInvariant();
        return BounceLocalParts.Contains(local, StringComparer.Ordinal);
    }

    /// <summary>主题是否像投递失败通知。</summary>
    public static bool MatchesBounceSubject(string? subject)
    {
        var text = (subject ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0) return false;
        return BounceSubjectKeywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));
    }

    /// <summary>主题是否只是延迟警告（仍在重试）。</summary>
    public static bool IsDelayNotice(string? subject)
    {
        var text = (subject ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0) return false;
        return DelaySubjectKeywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));
    }

    /// <summary>
    /// 是否退信。<paramref name="isDsnReport"/> 为 BODYSTRUCTURE 里判出的
    /// <c>multipart/report; report-type=delivery-status</c>；结构不可用时看发件人与主题双条件。
    /// </summary>
    public static bool IsBounce(
        string? fromEmail,
        string? fromName,
        string? subject,
        bool isDsnReport = false)
    {
        // 延迟警告优先排除：它同样是 delivery-status 报告，但邮件还在队列里重试
        if (IsDelayNotice(subject)) return false;

        if (isDsnReport) return true;

        return IsBounceSender(fromEmail, fromName) && MatchesBounceSubject(subject);
    }
}
