using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;

namespace ZungMail.Application.Services;

/// <summary>
/// 入库钩子：新邮件写入本地库后立即抽取业务标识（Job / SO / B/L / 箱号 …）。
/// 此时通常只有邮件头与摘要，正文下载完成后由 <see cref="MailReferenceService"/> 再重扫一次。
/// </summary>
public sealed class MailReferenceIngestHook : IMailIngestHook
{
    private readonly MailReferenceService _references;

    public MailReferenceIngestHook(MailReferenceService references) => _references = references;

    public Task OnMessagesIngestedAsync(IReadOnlyList<EmailDetail> messages, CancellationToken ct = default)
        => _references.IndexMessagesAsync(messages, ct);

    /// <summary>正文落地后重扫一次，把只出现在正文里的箱号 / 封号 / 船名补进业务索引。</summary>
    public Task OnBodiesDownloadedAsync(IReadOnlyList<EmailDetail> messages, CancellationToken ct = default)
        => _references.IndexMessagesAsync(messages, ct);
}
