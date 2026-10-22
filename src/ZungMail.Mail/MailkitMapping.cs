using ZungMail.Core.Domain;
using MailKit;
using MailKit.Security;
using CoreFlags = ZungMail.Core.Domain.MessageFlags;
using ImapFlags = MailKit.MessageFlags;

namespace ZungMail.Mail;

/// <summary>MailKit 与领域模型之间的映射：传输加密方式、文件夹语义分类、IMAP 标志位。</summary>
internal static class MailkitMapping
{
    /// <summary>传输加密映射。NFR-S1 要求默认强制 SSL，<see cref="SecureMode.None"/> 仍按「机会式 TLS」处理。</summary>
    public static SecureSocketOptions ToSocketOptions(SecureMode mode) => mode switch
    {
        SecureMode.Ssl => SecureSocketOptions.SslOnConnect,
        SecureMode.StartTls => SecureSocketOptions.StartTls,
        SecureMode.None => SecureSocketOptions.Auto,
        _ => SecureSocketOptions.SslOnConnect
    };

    /// <summary>
    /// 推断文件夹语义类型：优先使用服务器返回的 SPECIAL-USE 属性，
    /// 再按名称回退（国内服务商多数只给中文名，不给属性）。
    /// </summary>
    public static FolderKind InferKind(string fullName, string name, FolderAttributes attributes)
    {
        if ((attributes & FolderAttributes.Sent) != 0) return FolderKind.Sent;
        if ((attributes & FolderAttributes.Drafts) != 0) return FolderKind.Drafts;
        if ((attributes & FolderAttributes.Trash) != 0) return FolderKind.Trash;
        if ((attributes & FolderAttributes.Junk) != 0) return FolderKind.Junk;
        if ((attributes & FolderAttributes.Archive) != 0) return FolderKind.Archive;
        if ((attributes & FolderAttributes.All) != 0) return FolderKind.Archive;

        var lower = $"{fullName}/{name}".ToLowerInvariant();

        if (fullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) || lower.Contains("inbox"))
            return FolderKind.Inbox;
        if (ContainsAny(lower, "sent", "发件箱", "已发送", "已寄出", "寄件備份", "寄件备份"))
            return FolderKind.Sent;
        if (ContainsAny(lower, "draft", "草稿"))
            return FolderKind.Drafts;
        if (ContainsAny(lower, "trash", "deleted", "已删除", "已刪除", "回收站"))
            return FolderKind.Trash;
        if (ContainsAny(lower, "junk", "spam", "垃圾", "广告邮件"))
            return FolderKind.Junk;
        if (ContainsAny(lower, "archive", "归档", "歸檔", "存档", "歸檔郵件"))
            return FolderKind.Archive;

        return FolderKind.None;
    }

    /// <summary>服务器标志位 → 本地标志位。</summary>
    public static CoreFlags ToLocalFlags(ImapFlags? flags)
    {
        var value = flags ?? ImapFlags.None;
        var result = CoreFlags.None;
        if ((value & ImapFlags.Seen) != 0) result |= CoreFlags.Seen;
        if ((value & ImapFlags.Answered) != 0) result |= CoreFlags.Answered;
        if ((value & ImapFlags.Flagged) != 0) result |= CoreFlags.Flagged;
        if ((value & ImapFlags.Draft) != 0) result |= CoreFlags.Draft;
        if ((value & ImapFlags.Deleted) != 0) result |= CoreFlags.Deleted;
        return result;
    }

    /// <summary>本地标志位 → 服务器标志位。</summary>
    public static ImapFlags ToImapFlags(CoreFlags flags)
    {
        var result = ImapFlags.None;
        if ((flags & CoreFlags.Seen) != 0) result |= ImapFlags.Seen;
        if ((flags & CoreFlags.Answered) != 0) result |= ImapFlags.Answered;
        if ((flags & CoreFlags.Flagged) != 0) result |= ImapFlags.Flagged;
        if ((flags & CoreFlags.Draft) != 0) result |= ImapFlags.Draft;
        if ((flags & CoreFlags.Deleted) != 0) result |= ImapFlags.Deleted;
        return result;
    }

    /// <summary>本客户端会主动增删的标志位（\Recent 由服务器维护，不可写）。</summary>
    public static ImapFlags ManagedFlags =>
        ImapFlags.Seen | ImapFlags.Answered | ImapFlags.Flagged | ImapFlags.Draft | ImapFlags.Deleted;

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(text.Contains);
}
