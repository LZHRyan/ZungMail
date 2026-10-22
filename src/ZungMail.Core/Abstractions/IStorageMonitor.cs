using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>本地邮件存储监控：统计占用、磁盘余量与可释放空间，并给出分级预警。</summary>
public interface IStorageMonitor
{
    /// <summary>
    /// 获取当前存储快照。实现应使用聚合查询与文件元数据，避免递归扫描海量附件目录。
    /// </summary>
    Task<StorageSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}
