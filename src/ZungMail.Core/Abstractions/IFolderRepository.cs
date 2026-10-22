using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>文件夹仓储，负责维护增量同步状态（last_max_uid / UIDVALIDITY）。</summary>
public interface IFolderRepository
{
    Task<IReadOnlyList<MailFolder>> GetByAccountAsync(long accountId, CancellationToken ct = default);

    Task<IReadOnlyList<MailFolder>> GetAllAsync(CancellationToken ct = default);

    Task<MailFolder?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>按 (account_id, full_name) 插入或更新，返回主键。</summary>
    Task<long> UpsertAsync(MailFolder folder, CancellationToken ct = default);

    /// <summary>更新同步游标。</summary>
    Task UpdateSyncStateAsync(long folderId, uint uidValidity, uint lastMaxUid, DateTimeOffset syncedUtc, CancellationToken ct = default);

    /// <summary>UIDVALIDITY 变化时重置：清空 last_max_uid 与本地邮件，触发全量重同步。</summary>
    Task ResetFolderAsync(long folderId, uint newUidValidity, CancellationToken ct = default);

    Task UpdateCountsAsync(long folderId, int unreadCount, int totalCount, CancellationToken ct = default);

    Task<MailFolder?> FindByKindAsync(long accountId, FolderKind kind, CancellationToken ct = default);

    /// <summary>标记文件夹已在服务器端不存在。</summary>
    Task DeleteAsync(long folderId, CancellationToken ct = default);
}
