using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>本地发件人黑白名单持久化。</summary>
public interface ISenderFilterRepository
{
    /// <summary>读取全部条目（黑名单在前，再按模式排序）。</summary>
    Task<IReadOnlyList<SenderFilter>> GetAllAsync(CancellationToken ct = default);

    /// <summary>按 (pattern, is_blacklist) 幂等写入，返回主键；重复添加只更新备注。</summary>
    Task<long> UpsertAsync(SenderFilter filter, CancellationToken ct = default);

    Task<bool> DeleteAsync(long id, CancellationToken ct = default);
}
