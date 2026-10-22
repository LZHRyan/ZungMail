namespace ZungMail.Core.Domain;

/// <summary>
/// 文件夹（FR-03）。<see cref="LastMaxUid"/> 与 <see cref="UidValidity"/> 是增量同步的核心状态：
/// 仅拉取 UID &gt; LastMaxUid；UIDVALIDITY 变化时触发全量重同步。
/// </summary>
public sealed class MailFolder
{
    public long Id { get; set; }
    public long AccountId { get; set; }

    /// <summary>IMAP 邮箱完整路径，例如 INBOX、Sent Messages。</summary>
    public string FullName { get; set; } = "";

    /// <summary>界面展示名。</summary>
    public string DisplayName { get; set; } = "";

    public FolderKind Kind { get; set; } = FolderKind.None;

    /// <summary>IMAP UIDVALIDITY，变化即需重建。</summary>
    public uint UidValidity { get; set; }

    /// <summary>已完成同步的最大 UID。</summary>
    public uint LastMaxUid { get; set; }

    public DateTimeOffset? LastSyncUtc { get; set; }

    public int UnreadCount { get; set; }
    public int TotalCount { get; set; }

    /// <summary>该文件夹是否可选择（\Noselect 的容器文件夹不可选）。</summary>
    public bool IsSelectable { get; set; } = true;

    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? FullName : DisplayName;
}
