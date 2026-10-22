namespace ZungMail.Core.Domain;

/// <summary>
/// 本地运行日志的保留策略：数据目录 <c>logs\</c> 下的 <c>app-日期.log</c> 只保留最近 N 天，
/// 超期文件由后台自动删除，避免日志目录随使用时间无限增长。
/// <para>
/// 策略集中在这里而不是散落在删文件的代码里：设置页的可选范围、默认值与判定边界都只认这一处。
/// </para>
/// </summary>
public static class LogRetentionPolicy
{
    /// <summary>最少保留 1 天（只留当天）。</summary>
    public const int MinDays = 1;

    /// <summary>最多保留 7 天：日志只用于就近排查，再久的历史价值不高还占盘。</summary>
    public const int MaxDays = 7;

    /// <summary>默认保留 7 天。</summary>
    public const int DefaultDays = MaxDays;

    /// <summary>
    /// 把设置值夹到 1–7 天。0 与负数视为「未设置」，回落到默认 7 天——
    /// 不能当成「只留今天」，否则配置文件被写坏一次就会把一周的排查依据删掉。
    /// </summary>
    public static int Normalize(int days) => days <= 0 ? DefaultDays : Math.Clamp(days, MinDays, MaxDays);

    /// <summary>
    /// 保留 N 天 = 「今天 + 之前 N-1 天」，返回最早仍然保留的日期。
    /// 例：今天 2026-10-21、保留 7 天 → 最早保留 2026-10-15，10-14 及更早视为超期。
    /// </summary>
    public static DateOnly EarliestKeptDate(DateOnly today, int keepDays)
        => today.AddDays(-(Normalize(keepDays) - 1));

    /// <summary>该日期的日志文件是否已超期（早于最早保留日期）。</summary>
    public static bool IsExpired(DateOnly fileDate, DateOnly today, int keepDays)
        => fileDate < EarliestKeptDate(today, keepDays);
}
