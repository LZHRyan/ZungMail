using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>
/// 「等待回复」跟进仓储。
/// <para>
/// 一期只保存必要的判定输入（会话键、需回复地址集合、检查时间），
/// 回复情况在检查时实时从 <c>messages</c> 里推导，避免回复表与邮件表双写不一致。
/// </para>
/// </summary>
public interface IMailFollowUpRepository
{
    Task<long> UpsertAsync(MailFollowUp followUp, CancellationToken ct = default);

    Task<MailFollowUp?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>按状态列出，最近创建的在前；<paramref name="state"/> 为空表示全部。</summary>
    Task<IReadOnlyList<MailFollowUp>> GetAllAsync(
        FollowUpState? state = null,
        int take = 100,
        CancellationToken ct = default);

    /// <summary>到点需要检查的跟进（等待中且 next_check_utc &lt;= now）。</summary>
    Task<IReadOnlyList<MailFollowUp>> GetDueAsync(DateTimeOffset now, int take = 20, CancellationToken ct = default);

    /// <summary>同一会话里在指定时间之后的入站邮件（已排除已删除邮件与源邮件本身）。</summary>
    Task<IReadOnlyList<FollowUpReply>> GetThreadMessagesAfterAsync(
        string threadId,
        long excludeEmailId,
        DateTimeOffset afterUtc,
        CancellationToken ct = default);

    Task SetStateAsync(long id, FollowUpState state, DateTimeOffset utc, CancellationToken ct = default);

    /// <summary>重新排期（继续跟进）：写入新的检查时间并清空已提醒标记。</summary>
    Task RescheduleAsync(long id, DateTimeOffset nextCheckUtc, DateTimeOffset utc, CancellationToken ct = default);

    Task MarkNotifiedAsync(long id, DateTimeOffset utc, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    Task<int> CountActiveAsync(CancellationToken ct = default);
}
