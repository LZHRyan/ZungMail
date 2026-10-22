using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ZungMail.Mail;

/// <summary>
/// 草稿箱写入：把写信窗口的内容落成一封「草稿」邮件。
/// <para>
/// 顺序遵循「先本地、再服务器」：本地行立刻出现在草稿箱（离线也能看到），
/// 随后尽力 APPEND 到 IMAP 草稿箱；服务器不支持 UIDPLUS 或离线时只保留本地草稿，
/// 下一次保存会用新的本地行替换旧的，不会攒出一堆重复草稿。
/// </para>
/// </summary>
public sealed class MailDraftService : IMailDraftService
{
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly ImapSyncService _imap;
    private readonly ILogger<MailDraftService> _logger;

    public MailDraftService(
        IAccountRepository accounts,
        IFolderRepository folders,
        IMessageRepository messages,
        ImapSyncService imap,
        ILogger<MailDraftService> logger)
    {
        _accounts = accounts;
        _folders = folders;
        _messages = messages;
        _imap = imap;
        _logger = logger;
    }

    public async Task<long?> SaveAsync(DraftMailPayload payload, long? existingDraftId = null, CancellationToken ct = default)
    {
        try
        {
            var account = await _accounts.GetAsync(payload.AccountId, ct).ConfigureAwait(false);
            if (account is null) return null;

            var draftsFolder = await _folders.FindByKindAsync(account.Id, FolderKind.Drafts, ct).ConfigureAwait(false);
            if (draftsFolder is null)
            {
                _logger.LogWarning("账号 {Account} 还没有草稿箱文件夹，草稿只保留在写信窗口", account.Email);
                return null;
            }

            // 同一窗口再次保存：先清掉上一份（本地 + 服务器），避免草稿箱里堆重复
            string? previousFolderPath = null;
            uint previousUid = 0;
            if (existingDraftId is { } previousId)
            {
                var previous = await _messages.GetDetailAsync(previousId, ct).ConfigureAwait(false);
                if (previous is not null)
                {
                    previousUid = previous.Uid;
                    previousFolderPath = (await _folders.GetAsync(previous.FolderId, ct).ConfigureAwait(false))?.FullName;
                }
                await _messages.DeleteAsync(previousId, ct).ConfigureAwait(false);
            }

            using var message = BuildMessage(account, payload);
            var serverUid = await _imap.AppendDraftAsync(account, message, ct).ConfigureAwait(false);

            var to = MailAddressParser.ParseList(payload.To);
            var cc = MailAddressParser.ParseList(payload.Cc);
            var bcc = MailAddressParser.ParseList(payload.Bcc);
            var text = HtmlTextExtractor.ToPlainText(payload.BodyHtml);
            var messageId = string.IsNullOrWhiteSpace(message.MessageId)
                ? $"<draft-{Guid.NewGuid():N}@zungmail.local>"
                : message.MessageId;

            var detail = new EmailDetail
            {
                AccountId = account.Id,
                FolderId = draftsFolder.Id,
                // 服务器没给 UID 时用「负数 UID」占位：IMAP 的 UID 永远为正，不会和后续同步冲突
                Uid = serverUid ?? NextLocalUid(),
                MessageId = messageId,
                ThreadId = messageId,
                Subject = payload.Subject,
                FromName = string.IsNullOrWhiteSpace(account.SenderDisplayName) ? account.Email : account.SenderDisplayName,
                FromEmail = account.Email,
                To = to,
                Cc = cc,
                Bcc = bcc,
                DateUtc = DateTimeOffset.Now,
                Preview = BuildPreview(text),
                Flags = MessageFlags.Draft,
                Size = text.Length,
                HasAttachment = payload.AttachmentPaths.Count > 0,
                BodyState = BodyState.Downloaded,
                BodyHtml = payload.BodyHtml,
                BodyText = text,
                FolderName = draftsFolder.DisplayName,
            };

            var id = await _messages.UpsertAsync(detail, ct).ConfigureAwait(false);
            await _messages.SaveBodyAsync(id, payload.BodyHtml, text, BuildAttachments(payload.AttachmentPaths), ct)
                .ConfigureAwait(false);

            // 服务器上那份旧草稿要等新草稿落库后再删，避免中途失败把草稿弄丢
            if (previousUid > 0 && !string.IsNullOrEmpty(previousFolderPath))
                await _imap.DeleteRemoteAsync(account, previousFolderPath, previousUid, ct).ConfigureAwait(false);

            return id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存草稿到草稿箱失败");
            return null;
        }
    }

    public async Task DeleteAsync(long draftId, CancellationToken ct = default)
    {
        try
        {
            var detail = await _messages.GetDetailAsync(draftId, ct).ConfigureAwait(false);
            if (detail is null) return;

            await _messages.DeleteAsync(draftId, ct).ConfigureAwait(false);

            if (detail.Uid == 0 || (detail.Uid & 0x80000000) != 0) return; // 本地占位草稿没有服务器副本

            var account = await _accounts.GetAsync(detail.AccountId, ct).ConfigureAwait(false);
            var folder = await _folders.GetAsync(detail.FolderId, ct).ConfigureAwait(false);
            if (account is null || folder is null) return;

            await _imap.DeleteRemoteAsync(account, folder.FullName, detail.Uid, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除草稿失败：{DraftId}", draftId);
        }
    }

    private static MimeMessage BuildMessage(Account account, DraftMailPayload payload)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(account.SenderDisplayName) ? account.Email : account.SenderDisplayName,
            account.Email));

        AddRecipients(message.To, payload.To);
        AddRecipients(message.Cc, payload.Cc);
        AddRecipients(message.Bcc, payload.Bcc);

        message.Subject = payload.Subject ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(payload.InReplyTo)) message.InReplyTo = payload.InReplyTo;
        if (!string.IsNullOrWhiteSpace(payload.References)) message.References.Add(payload.References);

        var body = new BodyBuilder { HtmlBody = payload.BodyHtml ?? string.Empty };
        foreach (var path in payload.AttachmentPaths)
        {
            try
            {
                if (File.Exists(path)) body.Attachments.Add(path);
            }
            catch (Exception)
            {
                // 单个附件读不到就跳过，草稿本身仍要保存
            }
        }

        message.Body = body.ToMessageBody();
        return message;
    }

    private static void AddRecipients(InternetAddressList target, string? raw)
    {
        foreach (var address in MailAddressParser.ParseList(raw))
        {
            if (string.IsNullOrWhiteSpace(address.Address)) continue;
            target.Add(new MailboxAddress(address.Name ?? string.Empty, address.Address));
        }
    }

    private static List<AttachmentInfo> BuildAttachments(IReadOnlyList<string> paths)
    {
        var list = new List<AttachmentInfo>();
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                list.Add(new AttachmentInfo
                {
                    FileName = info.Name,
                    Size = info.Length,
                    LocalPath = info.FullName,
                });
            }
            catch (Exception)
            {
                // 路径无效时忽略
            }
        }
        return list;
    }

    private static string BuildPreview(string text)
    {
        var flat = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= 120 ? flat : flat[..120];
    }

    /// <summary>本地草稿占位 UID：置最高位，永远落在 IMAP 不会用到的数值区间。</summary>
    private static uint NextLocalUid()
        => 0x80000000u | (uint)(Random.Shared.Next(1, int.MaxValue));
}
