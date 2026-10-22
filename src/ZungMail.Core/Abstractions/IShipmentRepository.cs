using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>
/// 货代一票货（案件）与时效节点仓储。
/// <para>
/// 案件以业务编号 / 订舱号 / 提单号 / 箱号为别名聚合：一封邮件按它抽出的单号
/// 自动挂到某个案件上，同一票货的邮件、附件、时效节点即可在一页还原。
/// </para>
/// </summary>
public interface IShipmentRepository
{
    /// <summary>
    /// 批量写入：刷新这批邮件的时效节点，并按业务标识把它们挂到对应的案件上。
    /// 同一事务内完成，重复调用幂等（先删后插）。
    /// </summary>
    Task ApplyAsync(IReadOnlyList<ShipmentIndexEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// 时效看板：按到期时间升序返回即将到期与刚刚超时的节点。
    /// <paramref name="fromUtc"/> 默认为「现在 - 24 小时」，让刚错过的节点也能被看到。
    /// </summary>
    Task<IReadOnlyList<DeadlineBoardItem>> GetUpcomingDeadlinesAsync(
        int take = 8,
        DateTimeOffset? fromUtc = null,
        CancellationToken ct = default);

    /// <summary>某封邮件的时效节点（详情卡使用）。</summary>
    Task<IReadOnlyList<DeadlineBoardItem>> GetDeadlinesForMessageAsync(long emailId, CancellationToken ct = default);

    /// <summary>最近有往来的案件，用于案件入口。</summary>
    Task<IReadOnlyList<ShipmentCaseSummary>> GetRecentCasesAsync(int take = 20, CancellationToken ct = default);

    /// <summary>案件详情：时间轴 + 时效节点 + 附件。</summary>
    Task<ShipmentCaseDetail?> GetCaseAsync(long caseId, CancellationToken ct = default);

    /// <summary>某封邮件所属案件 id；未归入案件返回 0。</summary>
    Task<long> GetCaseIdForMessageAsync(long emailId, CancellationToken ct = default);

    /// <summary>打开 / 关闭案件（关闭后不再进入时效看板）。</summary>
    Task SetCaseStatusAsync(long caseId, ShipmentCaseStatus status, CancellationToken ct = default);

    /// <summary>导出资料包所需的原始内容（eml 路径、正文、附件本地路径）。</summary>
    Task<ShipmentCaseExport?> GetCaseExportAsync(long caseId, CancellationToken ct = default);
}
