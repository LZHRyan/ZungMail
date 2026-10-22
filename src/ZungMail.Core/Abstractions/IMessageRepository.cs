using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>
/// 邮件仓储。列表查询一律分页（NFR-P4），全文搜索走 FTS5（FR-06），
/// 会话归并由 <c>thread_id</c> 完成（FR-05）。
/// </summary>
public interface IMessageRepository
{
    /// <summary>分页查询邮件头摘要。</summary>
    Task<PagedResult<EmailSummary>> QueryAsync(MessageQuery query, CancellationToken ct = default);

    /// <summary>按会话归并的分页查询。</summary>
    Task<PagedResult<ThreadGroup>> QueryThreadsAsync(MessageQuery query, CancellationToken ct = default);

    Task<EmailDetail?> GetDetailAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// 彻底删除一封邮件：正文 / 附件记录 + 本地落盘文件（正文 eml、附件副本）。
    /// 只做本地清理，服务器端 expunge 由同步层负责；返回实际删除的文件数。
    /// </summary>
    Task<int> DeletePermanentlyAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<EmailSummary>> GetThreadMessagesAsync(string threadId, CancellationToken ct = default);

    /// <summary>按 (folder_id, uid) 查询，用于避免重复插入。</summary>
    Task<EmailDetail?> FindByUidAsync(long folderId, uint uid, CancellationToken ct = default);

    /// <summary>按账户 + Message-ID 查询，用于导入去重（同一封邮件可能出现在多个文件夹）。</summary>
    Task<EmailDetail?> FindByMessageIdAsync(long accountId, string messageId, CancellationToken ct = default);

    /// <summary>插入或按 (folder_id, uid) 更新，返回主键。</summary>
    Task<long> UpsertAsync(EmailDetail message, CancellationToken ct = default);

    /// <summary>批量写入邮件头，显著减少同步时的往返开销。</summary>
    Task UpsertRangeAsync(IReadOnlyList<EmailDetail> messages, CancellationToken ct = default);

    /// <summary>
    /// 取给定地址中「已经认识」的那些：出现在通讯录里，或历史来信（任意账户 / 文件夹）里有过该发件人。
    /// 用于陌生发件人判定，返回归一（小写）后的地址集合；调用方需在本批邮件写库之前调用。
    /// </summary>
    Task<IReadOnlySet<string>> GetKnownSenderAddressesAsync(
        IReadOnlyCollection<string> addresses, CancellationToken ct = default);

    Task UpdateFlagsAsync(long id, MessageFlags flags, CancellationToken ct = default);

    /// <summary>批量更新标志位。</summary>
    Task UpdateFlagsRangeAsync(IReadOnlyList<(long Id, MessageFlags Flags)> updates, CancellationToken ct = default);

    /// <summary>
    /// 批量置本地已删除（<c>\Deleted</c>）：一条 <c>UPDATE … WHERE id IN (…)</c>。
    /// 批量移动 / 删除时用它替代逐封 UpdateFlagsAsync，避免几十封时在 UI 线程上卡住。
    /// </summary>
    Task MarkDeletedAsync(IReadOnlyList<long> messageIds, CancellationToken ct = default);

    /// <summary>
    /// 把当前视图内所有未读邮件标为已读（跨分页，不只当前页），
    /// 返回受影响邮件的 id、所属文件夹与新标志位。
    /// </summary>
    Task<IReadOnlyList<(long Id, long FolderId, MessageFlags Flags)>> MarkAllSeenAsync(
        MessageQuery query, CancellationToken ct = default);

    /// <summary>取某文件夹中 UID 大于给定值的所有 (uid, flags)，用于校准已读/星标。</summary>
    Task<IReadOnlyList<(uint Uid, MessageFlags Flags)>> GetFlagsAsync(long folderId, uint minUidExclusive, CancellationToken ct = default);

    Task<uint> GetMaxUidAsync(long folderId, CancellationToken ct = default);

    /// <summary>
    /// 取某文件夹中邮件时间不早于 <paramref name="sinceUtc"/>（null 表示不限时间）、正文尚未下载的邮件，
    /// 按邮件时间倒序返回 (id, uid)。供同步时按「正文预取窗口」补齐正文与附件。
    /// </summary>
    Task<IReadOnlyList<(long Id, uint Uid)>> GetPendingBodyDownloadsAsync(
        long folderId, DateTimeOffset? sinceUtc, int limit, CancellationToken ct = default);

    Task SaveBodyAsync(long id, string bodyHtml, string bodyText, IReadOnlyList<AttachmentInfo> attachments, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    Task DeleteByFolderAsync(long folderId, CancellationToken ct = default);

    /// <summary>删除未下载正文的邮件（缓存清理）。</summary>
    Task<int> ClearBodyCacheAsync(CancellationToken ct = default);

    Task<int> CountAsync(long? accountId = null, long? folderId = null, CancellationToken ct = default);

    Task<int> CountUnreadAsync(long? accountId = null, long? folderId = null, CancellationToken ct = default);

    /// <summary>
    /// 按文件夹统计本地邮件数 / 未读数，口径与列表一致（排除本地已删除 \Deleted）。
    /// 侧栏角标用它，避免服务器 STATUS 里「已移走但还没 EXPUNGE」的邮件让空文件夹一直挂角标。
    /// </summary>
    Task<IReadOnlyDictionary<long, (int Total, int Unread)>> GetFolderCountsAsync(CancellationToken ct = default);

    /// <summary>本地总占用字节数（邮件头 + 正文）。</summary>
    Task<long> GetStorageSizeAsync(CancellationToken ct = default);

    /// <summary>
    /// 重建全文索引（主题 / 发件人 / 收件人 / 正文 / 附件名），
    /// 用于版本升级后补齐历史数据，返回处理的邮件数。分批执行，可取消。
    /// </summary>
    Task<int> RebuildSearchIndexAsync(IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>按原 id 恢复一封被清理的邮件（撤销清理），附件记录与全文索引同步恢复。返回是否实际写入。</summary>
    Task<bool> RestoreAsync(EmailDetail message, CancellationToken ct = default);
}
