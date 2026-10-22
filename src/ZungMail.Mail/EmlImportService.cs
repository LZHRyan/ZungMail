using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ZungMail.Mail;

/// <summary>
/// .eml 导入（Foxmail / Outlook 等客户端导出的 RFC 822 文件）。
/// 逐封解析 → 写入邮件头与正文 → 附件落盘（复用与 IMAP 相同的目录结构）。
/// 以账户维度按 Message-ID 去重；缺少 Message-ID 时用文件内容哈希兜底，保证重复导入不产生副本。
/// </summary>
public sealed class EmlImportService : IMailImportService
{
    /// <summary>单个文件大小上限，避免误选超大文件导致内存暴涨。</summary>
    private const long MaxFileBytes = 100L * 1024 * 1024;

    private readonly IMessageRepository _messages;
    private readonly IFolderRepository _folders;
    private readonly IAppPaths _paths;
    private readonly ILogger<EmlImportService> _logger;

    public EmlImportService(
        IMessageRepository messages,
        IFolderRepository folders,
        IAppPaths paths,
        ILogger<EmlImportService> logger)
    {
        _messages = messages;
        _folders = folders;
        _paths = paths;
        _logger = logger;
    }

    public async Task<MailImportResult> ImportAsync(
        long accountId,
        long folderId,
        IReadOnlyList<string> files,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (files.Count == 0) return new MailImportResult(0, 0, Array.Empty<string>());

        var folder = await _folders.GetAsync(folderId, ct).ConfigureAwait(false);
        if (folder is null || folder.AccountId != accountId)
        {
            return new MailImportResult(
                0,
                0,
                new[] { "目标文件夹不存在，或不属于所选账户" });
        }

        var imported = 0;
        var duplicates = 0;
        var failures = new List<string>();
        var nextUid = await _messages.GetMaxUidAsync(folderId, ct).ConfigureAwait(false);
        var processed = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            try
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                {
                    failures.Add($"{Path.GetFileName(file)}：文件不存在");
                    continue;
                }

                if (info.Length > MaxFileBytes)
                {
                    failures.Add($"{Path.GetFileName(file)}：文件超过 100 MB，已跳过");
                    continue;
                }

                using var message = await LoadMessageAsync(file, ct).ConfigureAwait(false);

                var messageId = NormalizeMessageId(message.MessageId);
                if (messageId.Length == 0)
                {
                    // 无 Message-ID 的邮件用内容哈希兜底，重复导入同文件仍会被去重。
                    messageId = "sha256-" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, ct)))
                        .ToLowerInvariant() + "@import.local";
                }

                if (await _messages.FindByMessageIdAsync(accountId, messageId, ct).ConfigureAwait(false) is not null)
                {
                    duplicates++;
                    continue;
                }

                var detail = BuildDetail(accountId, folderId, ++nextUid, message, messageId, info.Length);
                var id = await _messages.UpsertAsync(detail, ct).ConfigureAwait(false);
                detail.Id = id;

                var body = await MimeMapper.ExtractBodyAsync(message, _paths, detail, ct).ConfigureAwait(false);
                await _messages.SaveBodyAsync(id, body.Html, body.Text, body.Attachments, ct).ConfigureAwait(false);

                imported++;
                _logger.LogInformation(
                    "导入邮件 {File} → 账户 {Account} 文件夹 {Folder}（{Count} 个附件）",
                    Path.GetFileName(file), accountId, folder.Title, body.Attachments.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "导入邮件 {File} 失败", file);
                failures.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
            finally
            {
                progress?.Report(processed);
            }
        }

        return new MailImportResult(imported, duplicates, failures);
    }

    private static EmailDetail BuildDetail(
        long accountId,
        long folderId,
        uint uid,
        MimeMessage message,
        string messageId,
        long size)
    {
        var from = FirstMailbox(message.From);
        var replyTo = FirstMailbox(message.ReplyTo);

        var detail = new EmailDetail
        {
            AccountId = accountId,
            FolderId = folderId,
            Uid = uid,
            MessageId = messageId,
            Subject = message.Subject ?? string.Empty,
            FromName = from?.Name ?? string.Empty,
            FromEmail = from?.Address ?? string.Empty,
            DateUtc = message.Date == default ? DateTimeOffset.UtcNow : message.Date,
            Preview = BuildPreview(message.TextBody),
            Flags = MessageFlags.Seen,          // 导入的历史邮件默认视为已读，避免污染未读角标
            Size = size,
            HasAttachment = message.Attachments.Any(),
            To = ToAddresses(message.To),
            Cc = ToAddresses(message.Cc),
            Bcc = ToAddresses(message.Bcc),
            MdnTo = (message.Headers[HeaderId.DispositionNotificationTo] ?? string.Empty).Trim()
        };

        if (replyTo is not null && !string.IsNullOrWhiteSpace(replyTo.Address))
        {
            detail.ReplyTo = new EmailAddress(replyTo.Address, replyTo.Name);
        }

        detail.References = message.References
            .Select(reference => NormalizeMessageId(reference))
            .Where(reference => reference.Length > 0)
            .ToList();
        detail.ThreadId = ThreadKeyBuilder.Build(detail.MessageId, detail.References, detail.Subject);
        return detail;
    }

    private static string NormalizeMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var trimmed = value.Trim().Trim('<', '>').Trim();
        return trimmed.Length == 0 ? string.Empty : trimmed;
    }

    private static MailboxAddress? FirstMailbox(InternetAddressList? list)
        => list?.Mailboxes.FirstOrDefault();

    private static List<EmailAddress> ToAddresses(InternetAddressList? list)
        => list is null
            ? new List<EmailAddress>()
            : list.Mailboxes
                .Select(mailbox => new EmailAddress(mailbox.Address, mailbox.Name))
                .ToList();

    /// <summary>读取 .eml：流只用于解析，返回的消息自持内容，调用方负责释放。</summary>
    private static async Task<MimeMessage> LoadMessageAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await MimeMessage.LoadAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>正文首行摘要（列表预览用），与 IMAP 同步的预览口径保持一致。</summary>
    private static string BuildPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var firstLine = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('>'));

        if (string.IsNullOrEmpty(firstLine)) return string.Empty;

        return firstLine.Length <= 120
            ? firstLine
            : firstLine[..120].TrimEnd() + "…";
    }

    /// <summary>供界面展示的进度描述（导入数量与跳过数量）。</summary>
    public static string Describe(MailImportResult result)
        => result.Summary + (result.Failures.Count == 0
            ? string.Empty
            : "；首个失败：" + result.Failures[0] + string.Format(CultureInfo.CurrentCulture, "（共 {0} 个）", result.Failures.Count));
}
