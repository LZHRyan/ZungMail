namespace ZungMail.Core.Domain;

/// <summary>文件夹语义类型，用于识别特殊文件夹。</summary>
public enum FolderKind
{
    None = 0,
    Inbox = 1,
    Sent = 2,
    Drafts = 3,
    Trash = 4,
    Junk = 5,
    Archive = 6
}

/// <summary>
/// 服务器回写的「移动」结果。三种结局要分开处理：
/// 成功要删本地旧行；暂时失败留在队列里重试；邮件已经在服务器上不见了则必须结案，
/// 否则会变成每次启动/同步都重试一遍的僵尸项（实测 32 条卡了很久）。
/// </summary>
public enum MovePushResult
{
    /// <summary>服务器已确认移动。</summary>
    Moved = 0,

    /// <summary>暂时无法确认（离线、超时、鉴权失败）：保留在待同步队列，下次重试。</summary>
    Retryable = 1,

    /// <summary>服务器明确拒绝或邮件已不存在（UID 失效、已被其它客户端移走/删除）：视为终态。</summary>
    MessageGone = 2
}

/// <summary>
/// 账户级收取策略：决定同步时自动下载到什么程度。
/// <see cref="Inherit"/> 跟随设置页的「默认收取策略」，其余三档覆盖它。
/// </summary>
public enum FetchPolicy
{
    /// <summary>继承全局默认（老账户迁移后的值，行为与升级前一致）。</summary>
    Inherit = 0,

    /// <summary>完整收取：正文 + 附件全部落盘。</summary>
    Full = 1,

    /// <summary>仅收取正文：正文落盘，附件不落盘（需要时再单独下载）。</summary>
    BodyOnly = 2,

    /// <summary>仅收取邮件头：正文与附件都按需下载。</summary>
    HeadersOnly = 3
}

/// <summary>传输加密方式。按 NFR-S1，默认强制 SSL。</summary>
public enum SecureMode
{
    Ssl = 0,
    StartTls = 1,
    None = 2
}

/// <summary>登录凭据类型。</summary>
public enum AuthKind
{
    AuthCode = 0,
    Password = 1,
    OAuth2 = 2
}

/// <summary>邮件 IMAP 标志位（与 IMAP FLAGS 对齐）。</summary>
[Flags]
public enum MessageFlags
{
    None = 0,
    Seen = 1,
    Answered = 2,
    Flagged = 4,
    Draft = 8,
    Deleted = 32
}

/// <summary>正文下载状态：默认只同步邮件头，正文按需下载。</summary>
public enum BodyState
{
    NotDownloaded = 0,
    Downloaded = 1
}

/// <summary>发件箱条目状态。</summary>
public enum OutboxState
{
    Pending = 0,
    Sending = 1,
    Sent = 2,
    Failed = 3,

    /// <summary>
    /// 已提交 SMTP 但未确认落库（进程中断或状态回写失败）：服务器可能已接受该邮件。
    /// 不参与自动重试，需用户确认后再手动重试。
    /// </summary>
    Uncertain = 4
}

/// <summary>同步阶段。</summary>
public enum SyncPhase
{
    Idle = 0,
    Connecting = 1,
    Folders = 2,
    Headers = 3,
    Flags = 4,
    Bodies = 5,
    Completed = 6,
    Failed = 7,
    Cancelled = 8
}

/// <summary>列表过滤维度。</summary>
public enum MailListFilter
{
    All = 0,
    Unread = 1,
    Starred = 2,
    Attachments = 3
}

/// <summary>需要回写服务器的本地操作类型。</summary>
public enum PendingOperationKind
{
    /// <summary>已读 / 未读。</summary>
    MarkSeen = 0,

    /// <summary>星标。</summary>
    Flagged = 1,

    /// <summary>删除（服务器端 \Deleted + EXPUNGE）。</summary>
    Deleted = 2,

    /// <summary>批量已读（<see cref="PendingOperation.EmailId"/> 为 0，明细在 Payload）。</summary>
    MarkSeenBatch = 3,

    /// <summary>移动到语义文件夹（归档、垃圾邮件、垃圾箱、收件箱等）；目标见 Payload。</summary>
    Move = 4
}

/// <summary>待同步操作状态机。</summary>
public enum PendingOperationState
{
    Pending = 0,
    Syncing = 1,
    Succeeded = 2,
    Failed = 3
}
