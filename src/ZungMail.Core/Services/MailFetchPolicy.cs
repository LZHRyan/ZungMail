using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>解析后的收取策略：同步时自动下载到什么程度。</summary>
/// <param name="PrefetchBody">是否自动下载正文。</param>
/// <param name="BodyFloorUtc">正文预取的日期下限；<c>null</c> 表示不限日期（配合 <paramref name="PrefetchBody"/> 使用）。</param>
/// <param name="SaveAttachments">正文下载时是否把附件落盘。</param>
public readonly record struct ResolvedFetchPolicy(bool PrefetchBody, DateTimeOffset? BodyFloorUtc, bool SaveAttachments)
{
    /// <summary>只收邮件头：什么都不预取。</summary>
    public static ResolvedFetchPolicy HeadersOnly => new(false, null, false);
}

/// <summary>
/// 账户级收取策略：把「继承默认 / 完整收取 / 仅收取正文 / 仅收取邮件头」解析成
/// 同步真正要用的三个参数（是否预取正文、正文日期下限、是否落盘附件）。
///
/// 完整收取 = 不限日期地预取；仅收取正文 = 沿用全局窗口但不落盘附件；
/// 仅收取邮件头 = 不预取；继承默认则完全按设置页的「默认收取策略」。
/// </summary>
public static class MailFetchPolicy
{
    /// <summary>解析账户策略；<paramref name="globalPrefetchDays"/> 来自设置页的默认策略。</summary>
    public static ResolvedFetchPolicy Resolve(FetchPolicy policy, int globalPrefetchDays, DateTimeOffset now) => policy switch
    {
        FetchPolicy.Full => new ResolvedFetchPolicy(true, null, true),
        FetchPolicy.BodyOnly => ResolveBodyOnly(globalPrefetchDays, now),
        FetchPolicy.HeadersOnly => ResolvedFetchPolicy.HeadersOnly,
        _ => ResolveInherited(globalPrefetchDays, now)
    };

    private static ResolvedFetchPolicy ResolveInherited(int globalPrefetchDays, DateTimeOffset now)
    {
        var floor = AppSettings.BodyPrefetchFloor(globalPrefetchDays, now);
        return floor is null
            ? ResolvedFetchPolicy.HeadersOnly
            : new ResolvedFetchPolicy(true, floor, true);
    }

    private static ResolvedFetchPolicy ResolveBodyOnly(int globalPrefetchDays, DateTimeOffset now)
    {
        // 「仅收取正文」仍然要有时间窗口，否则几十万封历史邮件会一起下载
        var floor = AppSettings.BodyPrefetchFloor(globalPrefetchDays, now);
        return floor is null
            ? ResolvedFetchPolicy.HeadersOnly
            : new ResolvedFetchPolicy(true, floor, false);
    }

    /// <summary>文案：账户编辑里的下拉项。</summary>
    public static string Label(FetchPolicy policy) => policy switch
    {
        FetchPolicy.Full => "完整收取（正文 + 附件）",
        FetchPolicy.BodyOnly => "仅收取正文（附件按需下载）",
        FetchPolicy.HeadersOnly => "仅收取邮件头（正文按需下载）",
        _ => "继承默认策略"
    };
}
