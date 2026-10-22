using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>
/// 待同步操作队列仓储：持久化本地优先写操作（已读 / 星标 / 删除）的意图，
/// 让远端回写失败后仍可解释、可重试、可撤销，而不是依赖下一次同步「猜」。
/// </summary>
public interface IPendingOperationRepository
{
    /// <summary>
    /// 入队并返回主键。同一封邮件的同类未成功操作会被覆盖（保留最新一次意图），
    /// 避免离线期间反复切换状态导致队列无限增长。
    /// </summary>
    Task<long> EnqueueAsync(PendingOperation operation, CancellationToken ct = default);

    /// <summary>
    /// 批量入队（单事务）：逐封入队要开 N 次事务与 N 次去重查询，几十封时会在调用线程上卡住。
    /// 去重规则与单条一致：同一封邮件的同类未成功操作只保留最新意图。
    /// </summary>
    Task<IReadOnlyList<long>> EnqueueRangeAsync(
        IReadOnlyList<PendingOperation> operations, CancellationToken ct = default);

    Task<PendingOperation?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>尚未成功的操作（Pending / Syncing / Failed），按入队顺序返回。</summary>
    Task<IReadOnlyList<PendingOperation>> GetOutstandingAsync(CancellationToken ct = default);

    Task<int> CountOutstandingAsync(CancellationToken ct = default);

    /// <summary>更新状态机；<paramref name="attempts"/> 为累计尝试次数。</summary>
    Task UpdateStateAsync(
        long id,
        PendingOperationState state,
        string? error = null,
        int attempts = 0,
        CancellationToken ct = default);

    /// <summary>移除操作（撤销或确认完成后）。</summary>
    Task RemoveAsync(long id, CancellationToken ct = default);

    /// <summary>清理已成功且早于给定时间的记录，控制表体积。</summary>
    Task<int> PruneSucceededAsync(DateTimeOffset olderThanUtc, CancellationToken ct = default);
}
