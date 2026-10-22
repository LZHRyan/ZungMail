using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>标签持久化与邮件-标签关联。</summary>
public interface IMailTagRepository
{
    Task<IReadOnlyList<MailTag>> GetAllAsync(CancellationToken ct = default);

    /// <summary>取某个账户的标签（标签按账户隔离）。</summary>
    Task<IReadOnlyList<MailTag>> GetByAccountAsync(long accountId, CancellationToken ct = default);

    Task<long> UpsertAsync(MailTag tag, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>取某封邮件已打的标签。</summary>
    Task<IReadOnlyList<MailTag>> GetForMessageAsync(long emailId, CancellationToken ct = default);

    /// <summary>批量取多封邮件的标签，用于列表徽标。</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<MailTag>>> GetForMessagesAsync(
        IReadOnlyList<long> emailIds, CancellationToken ct = default);

    Task AddToMessageAsync(long emailId, long tagId, CancellationToken ct = default);

    Task RemoveFromMessageAsync(long emailId, long tagId, CancellationToken ct = default);

    /// <summary>按标签查询邮件 id（分页由调用方的 MessageQuery 负责排序）。</summary>
    Task<IReadOnlyList<long>> GetMessageIdsAsync(long tagId, CancellationToken ct = default);
}

/// <summary>待办持久化。</summary>
public interface IMailTodoRepository
{
    Task<IReadOnlyList<MailTodo>> QueryAsync(MailTodoState? state = null, bool overdueOnly = false, CancellationToken ct = default);

    Task<MailTodo?> GetByEmailAsync(long emailId, CancellationToken ct = default);

    Task<long> UpsertAsync(MailTodo todo, CancellationToken ct = default);

    Task SetStateAsync(long id, MailTodoState state, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    Task<int> CountOpenAsync(CancellationToken ct = default);

    Task<int> CountOverdueAsync(CancellationToken ct = default);

    /// <summary>取已到期且尚未提醒过的待办（用于托盘提醒）。</summary>
    Task<IReadOnlyList<MailTodo>> GetDueForReminderAsync(DateTimeOffset now, int take = 20, CancellationToken ct = default);

    /// <summary>标记/清除提醒时间：传 null 表示重置（截止时间变更后应重新提醒）。</summary>
    Task SetRemindedAsync(long id, DateTimeOffset? remindedUtc, CancellationToken ct = default);
}
