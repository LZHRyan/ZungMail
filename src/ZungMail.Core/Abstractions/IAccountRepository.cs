using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>账户仓储（FR-01）。凭据字段落库前必须加密。</summary>
public interface IAccountRepository
{
    Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default);

    Task<Account?> GetAsync(long id, CancellationToken ct = default);

    Task<long> AddAsync(Account account, CancellationToken ct = default);

    Task UpdateAsync(Account account, CancellationToken ct = default);

    /// <summary>删除账户；<paramref name="deleteMessages"/> 为 true 时级联删除本地邮件缓存。</summary>
    Task DeleteAsync(long id, bool deleteMessages, CancellationToken ct = default);

    /// <summary>启用 / 停止使用账户（停止后不参与同步与自动发送）。</summary>
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// 把指定账户设为主账户（同时清掉其它账户的主账户标记，保证只有一个主账户）。
    /// 主账户在同步顺序与账户列表里排最前。
    /// </summary>
    Task SetPrimaryAsync(long id, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
}
