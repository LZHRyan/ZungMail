using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>侧边栏“往来邮件”条目（轻量投影，避免整封详情开销）。</summary>
public sealed record CorrespondenceItem(
    long Id,
    string Subject,
    string FromName,
    DateTimeOffset DateUtc,
    bool IsUnread,
    long FolderId,
    string Preview = "",
    bool HasAttachment = false)
{
    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? "（无主题）" : Subject;

    public string DateDisplay => DisplayFormat.RelativeDate(DateUtc);

    /// <summary>卡片上的时间：带时分的相对时间，同一天连着几封也能看出先后。</summary>
    public string DateDetailDisplay => DisplayFormat.RelativeDateTime(DateUtc);

    /// <summary>正文摘要（同步时落库的一行预览）；没有预览时卡片只显示主题。</summary>
    public string PreviewDisplay => string.IsNullOrWhiteSpace(Preview) ? string.Empty : Preview.Trim();

    public bool HasPreview => PreviewDisplay.Length > 0;
}

/// <summary>侧边栏“同发件人附件”条目。</summary>
public sealed record SenderAttachmentItem(
    long MessageId,
    long AttachmentId,
    string FileName,
    long Size,
    string? LocalPath,
    string Subject,
    DateTimeOffset DateUtc)
{
    public string SizeDisplay => DisplayFormat.FileSize(Size);

    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? "（无主题）" : Subject;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath);
}

/// <summary>邮件上下文侧边栏数据：同一发件人的往来邮件与附件。</summary>
public interface IMailContextRepository
{
    Task<IReadOnlyList<CorrespondenceItem>> GetCorrespondenceAsync(
        long accountId, string fromEmail, long excludeMessageId, int take = 50, CancellationToken ct = default);

    Task<IReadOnlyList<SenderAttachmentItem>> GetSenderAttachmentsAsync(
        long accountId, string fromEmail, int take = 50, CancellationToken ct = default);
}
