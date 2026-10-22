using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>邮件备注（本地批注）：纯本地数据，不回写 IMAP，也不随邮件同步。</summary>
public interface IMessageNoteRepository
{
    /// <summary>取某封邮件的备注；没有则返回空字符串。</summary>
    Task<string> GetAsync(long emailId, CancellationToken ct = default);

    /// <summary>保存备注；内容为空时删除记录。</summary>
    Task SaveAsync(long emailId, string note, CancellationToken ct = default);

    /// <summary>批量取备注非空的邮件 id，用于列表标记。</summary>
    Task<IReadOnlySet<long>> GetNotedMessageIdsAsync(IReadOnlyList<long> emailIds, CancellationToken ct = default);
}
