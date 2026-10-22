using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>待扫描业务标识的邮件（尚未建立扫描记录，或正文晚于主题到达）。</summary>
public sealed record PendingReferenceScan(
    long EmailId,
    string Subject,
    string Preview,
    string BodyText,
    bool HasBody,
    DateTimeOffset DateUtc);

/// <summary>侧栏「业务单号」聚合项：同一标识（规范化后）关联的邮件数与最近时间。</summary>
public sealed record FreightReferenceSummary(
    FreightReferenceKind Kind,
    string Value,
    string Normalized,
    int MessageCount,
    DateTimeOffset LastUtc)
{
    public string KindLabel => FreightReference.KindLabelOf(Kind);

    public string KindShort => FreightReference.KindShortOf(Kind);

    public string KindColor => FreightReference.KindColorOf(Kind);

    /// <summary>点击后直接填入搜索框的过滤表达式。</summary>
    public string SearchText => $"{FreightReference.SearchKeywordOf(Kind)}:{Value}";

    public string CountDisplay => MessageCount.ToString();

    public string LastDisplay => DisplayFormat.RelativeDate(LastUtc);
}

/// <summary>批量写入项：一封邮件的标识集合、扫描状态与行业类型。</summary>
public sealed record ReferenceIndexEntry(
    long EmailId,
    bool BodyIndexed,
    IReadOnlyList<FreightReference> References,
    MailType MailType = MailType.Unknown,
    int MailTypeScore = 0);

/// <summary>
/// 货代业务标识仓储：保存从邮件中抽取的 Job / SO / B/L / 箱号等单号，
/// 并提供按标识聚合（一票货视图）和后台补扫的查询入口。
/// </summary>
public interface IMailReferenceRepository
{
    /// <summary>整体替换某封邮件的标识集合，并记录扫描状态（是否已覆盖正文）。</summary>
    Task ReplaceForMessageAsync(
        long emailId,
        IReadOnlyList<FreightReference> references,
        bool bodyIndexed,
        CancellationToken ct = default);

    /// <summary>批量替换（首次全量补扫时用单事务写入，避免逐封提交）。</summary>
    Task ReplaceRangeAsync(IReadOnlyList<ReferenceIndexEntry> entries, CancellationToken ct = default);

    Task<IReadOnlyList<FreightReference>> GetForMessageAsync(long emailId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<long, IReadOnlyList<FreightReference>>> GetForMessagesAsync(
        IReadOnlyList<long> emailIds,
        CancellationToken ct = default);

    /// <summary>按邮件数倒序返回高频业务标识，用于侧栏一票货聚合入口。</summary>
    Task<IReadOnlyList<FreightReferenceSummary>> GetTopAsync(
        long? accountId = null,
        int take = 12,
        CancellationToken ct = default);

    /// <summary>取一批待扫描邮件：没有扫描记录，或正文后到但尚未重新扫描。</summary>
    Task<IReadOnlyList<PendingReferenceScan>> GetPendingScansAsync(int take = 200, CancellationToken ct = default);

    Task<int> CountPendingScansAsync(CancellationToken ct = default);

    /// <summary>正文更新后把扫描记录标记为「未覆盖正文」，等待后台重扫。</summary>
    Task InvalidateAsync(long emailId, CancellationToken ct = default);

    /// <summary>
    /// 清空全部扫描进度，让后台补扫把历史邮件重抽一遍。
    /// 货代功能「关闭 → 重新开启」时调用：关闭期间入库的邮件没有被抽取，需要补扫补齐。
    /// </summary>
    Task ClearScanProgressAsync(CancellationToken ct = default);
}
