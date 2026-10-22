namespace ZungMail.Core.Abstractions;

/// <summary>
/// 写信窗口要写进「草稿箱」的内容。附件用本地绝对路径表示（草稿保存时只记录路径，
/// 恢复时文件不存在就跳过），与写信窗口的 JSON 草稿保持一致。
/// </summary>
public sealed record DraftMailPayload(
    long AccountId,
    string To,
    string Cc,
    string Bcc,
    string Subject,
    string BodyHtml,
    IReadOnlyList<string> AttachmentPaths,
    string? InReplyTo = null,
    string? References = null);

/// <summary>
/// 草稿箱写入：把「还没发出的邮件」变成草稿箱里的一封草稿（本地先落库，再尽力 APPEND 到服务器）。
/// <para>
/// 与 <c>IComposeDraftStore</c>（本机 JSON、用于关窗恢复）分工不同：
/// 这里写的是真正的邮件行，用户能在「草稿箱」里看到、双击继续编辑；发送成功后由调用方删除。
/// </para>
/// </summary>
public interface IMailDraftService
{
    /// <summary>
    /// 保存草稿并返回本地邮件 id（没有草稿箱文件夹、账户不可用时返回 null）。
    /// 传入 <paramref name="existingDraftId"/> 时先删掉旧草稿再写新的，保证同一窗口只有一封草稿。
    /// </summary>
    Task<long?> SaveAsync(DraftMailPayload payload, long? existingDraftId = null, CancellationToken ct = default);

    /// <summary>删除草稿：本地立即删除，服务器副本尽力而为（UID 不可用或离线时忽略）。</summary>
    Task DeleteAsync(long draftId, CancellationToken ct = default);
}
