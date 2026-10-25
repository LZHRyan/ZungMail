using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>
/// 历史邮件清理引擎：预览、执行、撤销、彻底删除、中断恢复与自动清理。
/// 清理采用「隔离区」机制：邮件从活动库移入 quarantine 目录并记录审计快照，
/// 保留期内可完整撤销；彻底删除后才真正释放磁盘空间。
/// </summary>
public interface ICleanupService
{
    /// <summary>
    /// 按条件预览可清理邮件（分页：跳过 <paramref name="skip"/> 条后取 <paramref name="take"/> 条），
    /// 同时返回总量与预计可释放字节统计。
    /// </summary>
    Task<CleanupPreview> PreviewAsync(
        CleanupFilter filter, int take = 200, int skip = 0, CancellationToken ct = default);

    /// <summary>
    /// 只取一页预览行（不重算 COUNT/SUM）。汇总由调用方按筛选条件缓存，翻页时用它避免重复全表聚合。
    /// </summary>
    Task<IReadOnlyList<CleanupPreviewItem>> PreviewItemsAsync(
        CleanupFilter filter, int take = 50, int skip = 0, CancellationToken ct = default);

    /// <summary>按条件取出全部匹配邮件的 id（供「全选 / 反选」使用，只取 id 不取正文）。</summary>
    Task<IReadOnlyList<long>> GetMatchingIdsAsync(CleanupFilter filter, CancellationToken ct = default);

    /// <summary>
    /// 执行清理。<paramref name="selectedIds"/> 非空时按手动勾选的 id 清理，否则按筛选条件全量清理。
    /// 支持进度回调与取消；取消后任务标记为「已中断」，已完成部分可撤销或恢复。
    /// </summary>
    Task<CleanupJob> ExecuteAsync(
        CleanupFilter filter,
        IReadOnlyCollection<long>? selectedIds,
        CleanupMode mode,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>最近清理任务，按开始时间倒序。</summary>
    Task<IReadOnlyList<CleanupJob>> GetJobsAsync(int take = 30, CancellationToken ct = default);

    /// <summary>撤销一次清理：从审计快照恢复邮件记录与隔离区文件。</summary>
    Task<int> UndoAsync(long jobId, CancellationToken ct = default);

    /// <summary>彻底删除一个清理任务的隔离数据并释放磁盘空间，返回释放字节数。</summary>
    Task<long> PurgeAsync(long jobId, CancellationToken ct = default);

    /// <summary>清理超过保留期的隔离数据，返回释放字节数。</summary>
    Task<long> PurgeExpiredAsync(CancellationToken ct = default);

    /// <summary>恢复一个已中断的清理任务（按原条件继续），任务不存在或不可恢复时返回 null。</summary>
    Task<CleanupJob?> ResumeAsync(
        long jobId, IProgress<CleanupProgress>? progress = null, CancellationToken ct = default);
}
