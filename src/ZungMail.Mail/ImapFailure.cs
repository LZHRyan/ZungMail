using MailKit;
using MailKit.Net.Imap;

namespace ZungMail.Mail;

/// <summary>
/// 判断 IMAP 失败原因是否属于「服务器上已经没有这封邮件」。
///
/// 这类错误重试没有意义：本地库记的 UID 在服务器上已经失效（典型场景是同一邮箱在 Foxmail / 手机 / 网页
/// 里被移走或删掉了），继续重试只会让待同步队列越积越多、每次启动都白连一遍服务器。
/// 应用层据此把操作结案，而不是当成「稍后再试」。
/// </summary>
public static class ImapFailure
{
    /// <summary>邮件已不在服务器（UID 失效 / 已被其它客户端移走），或目标文件夹已不存在。</summary>
    public static bool IsMessageGone(Exception ex) => ex switch
    {
        MessageNotFoundException => true,
        FolderNotFoundException => true,
        ImapCommandException imap when imap.Response == ImapCommandResponse.No => IsRejectionAboutMessage(imap.Message),
        _ => false
    };

    /// <summary>
    /// IMAP 的 NO 响应文本因服务器而异，只认明确的「这批邮件搬不动」措辞，
    /// 其余 NO（配额不足、目标只读等）仍然按可重试处理，避免误结案。
    /// </summary>
    public static bool IsRejectionAboutMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        return message.Contains("can't move", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot move", StringComparison.OrdinalIgnoreCase)
            || message.Contains("can not move", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid uids", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no such message", StringComparison.OrdinalIgnoreCase);
    }
}
