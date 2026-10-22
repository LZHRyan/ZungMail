using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>
/// IMAP 增量同步引擎（FR-03）。实现须满足：
/// 后台线程执行、不阻塞 UI；仅拉取 UID &gt; last_max_uid；UIDVALIDITY 变化触发全量重同步；失败可重试且不丢数据。
/// </summary>
public interface IMailSyncService
{
    /// <summary>同步进度通知（可能在任意线程触发）。</summary>
    event EventHandler<SyncProgress>? ProgressChanged;

    Task<SyncOutcome> SyncAccountAsync(long accountId, bool fullResync = false, CancellationToken ct = default);

    Task<IReadOnlyList<SyncOutcome>> SyncAllAsync(bool fullResync = false, CancellationToken ct = default);

    /// <summary>
    /// 后台轮询用的一次全账户增量同步：收件箱每轮必刷，其余文件夹按轮次滚动遍历，
    /// 并受单轮时间预算约束（见 ZungMail.Mail.BackgroundSyncPolicy）。
    /// 与 <see cref="SyncAllAsync"/> 的区别是「不追求一轮刷完所有文件夹」。
    /// </summary>
    Task<IReadOnlyList<SyncOutcome>> SyncAllIncrementalAsync(CancellationToken ct = default);

    /// <summary>只同步收件箱：IMAP IDLE 探测到新邮件后立刻做一次，秒级可见。</summary>
    Task<SyncOutcome> SyncInboxAsync(long accountId, CancellationToken ct = default);

    /// <summary>测试 IMAP/SMTP 可用性（设置界面「测试连接」）。</summary>
    Task<bool> TestConnectionAsync(Account account, CancellationToken ct = default);

    /// <summary>按需下载某封邮件的正文与附件（仅拉取邮件头策略下使用）。</summary>
    Task<bool> DownloadBodyAsync(long emailId, CancellationToken ct = default);

    /// <summary>
    /// 按需下载某封邮件；<paramref name="includeAttachments"/> 为 true 时无论账户收取策略如何都把附件落盘
    /// （「仅收取正文」账户里用户点「下载附件」走这里）。
    /// </summary>
    Task<bool> DownloadBodyAsync(long emailId, bool includeAttachments, CancellationToken ct = default);

    /// <summary>
    /// 忽略本地「正文已下载」标记，重新向服务器拉一次正文（阅读区「重新获取正文」按钮用）。
    /// 用于正文实际为空、上次解析失败、正文缓存被清掉等情况 —— 这些邮件按
    /// <see cref="DownloadBodyAsync(long, CancellationToken)"/> 会被「已下载」直接短路，
    /// 正文永远补不回来。
    /// </summary>
    Task<bool> RedownloadBodyAsync(long emailId, CancellationToken ct = default);

    /// <summary>把本地标志位（已读/星标）回写服务器。</summary>
    Task<bool> PushFlagsAsync(long emailId, MessageFlags flags, CancellationToken ct = default);

    /// <summary>批量回写标志位（如「全部标为已读」）：按账户/文件夹合并连接，避免逐封建连。</summary>
    Task<int> PushFlagsBatchAsync(IReadOnlyList<(long EmailId, MessageFlags Flags)> updates, CancellationToken ct = default);

    /// <summary>
    /// 把邮件移动到语义文件夹。实现优先使用 IMAP MOVE，服务器不支持时回退 COPY + \\Deleted。
    /// 调用方负责先持久化本地意图；<see cref="MovePushResult.Retryable"/> 表示应保留在待同步队列中重试，
    /// <see cref="MovePushResult.MessageGone"/> 表示服务器上已经没有这封邮件（结案即可，不要重试）。
    /// </summary>
    Task<MovePushResult> MoveAsync(long emailId, FolderKind destination, CancellationToken ct = default);

    /// <summary>移动到指定账户下的任意可写文件夹（按完整 IMAP 路径定位目标）。</summary>
    Task<MovePushResult> MoveToFolderAsync(long emailId, long destinationAccountId, string destinationFullName, CancellationToken ct = default);

    /// <summary>
    /// 批量移动到同一目标文件夹：按源文件夹合并成一条连接，一条 <c>UID MOVE</c> 带走整批 UID，
    /// 避免逐封建连 + 逐封命令。返回每封邮件的结局（成功 / 可重试 / 服务器上已不存在）。
    /// </summary>
    Task<IReadOnlyDictionary<long, MovePushResult>> MoveToFolderBatchAsync(
        IReadOnlyList<long> emailIds,
        long destinationAccountId,
        string destinationFullName,
        CancellationToken ct = default);
}

/// <summary>阅读收条（MDN）：回复发件人的回执请求。</summary>
public interface IReadReceiptService
{
    Task SendAsync(Account account, EmailDetail original, CancellationToken ct = default);
}

/// <summary>阅读收条发送记录：同一封邮件只提示一次。</summary>
public interface IReadReceiptRepository
{
    Task<bool> IsSentAsync(long emailId, CancellationToken ct = default);

    Task MarkSentAsync(long emailId, CancellationToken ct = default);
}

/// <summary>SMTP 发送（FR-07），强制 SSL/TLS（NFR-S1）。</summary>
public interface IMailSendService
{
    Task SendAsync(Account account, OutboxItem item, CancellationToken ct = default);
}

/// <summary>待发送队列处理器（FR-08）：到点触发发送，失败记录原因并保留队列。</summary>
public interface IOutboxProcessor
{
    event EventHandler<OutboxItem>? ItemSent;

    event EventHandler<OutboxItem>? ItemFailed;

    /// <summary>
    /// 启动时回收中断的投递：把残留的「发送中」改为「发送结果未知」，并返回回收条数。
    /// 这些邮件可能已被服务器接受，因此不自动重发，交由用户核对「已发送」后决定是否重试。
    /// </summary>
    Task<int> RecoverInterruptedAsync(CancellationToken ct = default);

    /// <summary>处理所有到期的待发送条目，返回成功发送数量。</summary>
    Task<int> ProcessDueAsync(CancellationToken ct = default);
}
