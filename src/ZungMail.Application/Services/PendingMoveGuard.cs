using ZungMail.Core.Domain;

namespace ZungMail.Application.Services;

/// <summary>重试一条「移动」操作时的处置方式。</summary>
public enum PendingMoveOutcome
{
    /// <summary>本地还没生效：需要真的连服务器回写。</summary>
    Push,

    /// <summary>本地已经生效（邮件已在目标文件夹，或本地行已被清理）：队列项直接结案。</summary>
    AlreadyApplied
}

/// <summary>
/// 「移动」类待同步操作的重试前置判断。
///
/// 背景：移动是本地先行、异步回写。若第一次回写失败，本地状态往往已经改好（邮件已在垃圾箱 /
/// 已在目标文件夹；或回写成功后已同步又删掉了旧行），这时再去回写，服务端侧的比较会得到
/// 「源文件夹 == 目标文件夹」，永远返回失败——于是这条操作每次同步都被重试一遍、attempts 一路涨，
/// 却永远不会成功。这里先把这类「本地已生效」的项挑出来结案，避免制造僵尸计数。
/// </summary>
public static class PendingMoveGuard
{
    /// <summary>
    /// 判定一条移动操作是否还需要回写服务器。
    /// </summary>
    /// <param name="messageExists">本地是否还存在这条邮件（行被清理 / 账户删除后就没有可同步对象）。</param>
    /// <param name="localFolderId">邮件当前所在的本地文件夹 id。</param>
    /// <param name="localFolderKind">邮件当前所在文件夹的语义类型。</param>
    /// <param name="destinationFolderId">目标文件夹 id；&gt;0 表示移动到具体文件夹。</param>
    /// <param name="destinationKind">目标语义文件夹（目标不是具体文件夹时使用）。</param>
    public static PendingMoveOutcome Evaluate(
        bool messageExists,
        long localFolderId,
        FolderKind? localFolderKind,
        long destinationFolderId,
        FolderKind destinationKind)
    {
        if (!messageExists) return PendingMoveOutcome.AlreadyApplied;

        if (destinationFolderId > 0)
        {
            return localFolderId == destinationFolderId
                ? PendingMoveOutcome.AlreadyApplied
                : PendingMoveOutcome.Push;
        }

        return localFolderKind == destinationKind
            ? PendingMoveOutcome.AlreadyApplied
            : PendingMoveOutcome.Push;
    }

    /// <summary>语义文件夹的中文名，用于「待同步明细」文案。</summary>
    public static string KindLabel(FolderKind kind) => kind switch
    {
        FolderKind.Inbox => "收件箱",
        FolderKind.Sent => "已发送",
        FolderKind.Drafts => "草稿箱",
        FolderKind.Trash => "垃圾箱",
        FolderKind.Junk => "垃圾邮件",
        FolderKind.Archive => "邮件归档",
        _ => "文件夹"
    };
}
