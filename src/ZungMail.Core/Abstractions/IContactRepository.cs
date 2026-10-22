using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>联系人仓储（FR-10），以邮箱地址为唯一键。</summary>
public interface IContactRepository
{
    /// <summary>T9 式前缀匹配补全：先匹配名称前缀，再匹配邮箱前缀。</summary>
    Task<IReadOnlyList<Contact>> SuggestAsync(string prefix, int take = 8, CancellationToken ct = default);

    Task<IReadOnlyList<Contact>> GetAllAsync(string? filter = null, CancellationToken ct = default);

    /// <summary>
    /// 分页查询联系人（地址簿列表用）。<paramref name="filter"/> 为空时按使用频次排序，
    /// 否则按姓名 / 邮箱模糊匹配。返回当前页数据与命中总数。
    /// </summary>
    Task<PagedResult<Contact>> QueryAsync(
        string? filter, int skip, int take, CancellationToken ct = default);

    /// <summary>收/发信时自动沉淀联系人，重复地址只累加计数。</summary>
    Task CollectAsync(IEnumerable<EmailAddress> addresses, CancellationToken ct = default);

    /// <summary>
    /// 批量导入联系人：按邮箱插入，已存在时只刷新名称（不累加使用次数），返回实际写入条数。
    /// 用于地址簿导入，避免把「导入」算成一次往来。
    /// </summary>
    Task<int> UpsertManyAsync(IEnumerable<Contact> contacts, CancellationToken ct = default);

    Task SetWhitelistAsync(string email, bool whitelisted, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);
}

/// <summary>待发送队列仓储（FR-08）。</summary>
public interface IOutboxRepository
{
    /// <summary>返回已经到点、且不在失败退避等待中的条目。</summary>
    Task<IReadOnlyList<OutboxItem>> GetDueAsync(DateTimeOffset nowUtc, CancellationToken ct = default);

    Task<IReadOnlyList<OutboxItem>> GetAllAsync(CancellationToken ct = default);

    Task<OutboxItem?> GetAsync(long id, CancellationToken ct = default);

    Task<long> AddAsync(OutboxItem item, CancellationToken ct = default);

    /// <summary>
    /// 更新投递状态。<paramref name="nextAttemptUtc"/> 为下一次允许自动投递的最早时间；
    /// 传 null 表示取消退避限制（例如用户手动重试）。
    /// </summary>
    Task UpdateStateAsync(long id, OutboxState state, string? error, int attempts, DateTimeOffset? nextAttemptUtc = null, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);
}

/// <summary>记事本仓储（FR-11，P2）。</summary>
public interface INoteRepository
{
    Task<IReadOnlyList<Note>> GetAllAsync(string? filter = null, CancellationToken ct = default);

    Task<Note?> GetAsync(long id, CancellationToken ct = default);

    Task<long> SaveAsync(Note note, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);
}
