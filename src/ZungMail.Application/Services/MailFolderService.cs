using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.Application.Services;

/// <summary>文件夹操作失败的业务原因；界面据此给出可读提示。</summary>
public enum FolderOperationStatus
{
    Succeeded = 0,
    InvalidName,
    Duplicate,
    SystemFolderProtected,
    NotEmpty,
    NotFound,
    RemoteFailed
}

public sealed record FolderOperationResult(
    FolderOperationStatus Status,
    MailFolder? Folder = null,
    string? Error = null)
{
    public bool Success => Status == FolderOperationStatus.Succeeded;

    public static FolderOperationResult Ok(MailFolder? folder = null) => new(FolderOperationStatus.Succeeded, folder);

    public static FolderOperationResult Fail(FolderOperationStatus status, string error)
        => new(status, null, error);
}

/// <summary>
/// 应用层文件夹管理：名称校验、系统文件夹保护、服务器操作与本地索引的一致性维护。
/// </summary>
public sealed class MailFolderService
{
    private static readonly char[] InvalidNameCharacters = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly IMailFolderAdminService _admin;
    private readonly ILogger<MailFolderService> _logger;

    public MailFolderService(
        IFolderRepository folders,
        IMessageRepository messages,
        IMailFolderAdminService admin,
        ILogger<MailFolderService> logger)
    {
        _folders = folders;
        _messages = messages;
        _admin = admin;
        _logger = logger;
    }

    public async Task<FolderOperationResult> CreateAsync(
        long accountId, MailFolder? parent, string displayName, CancellationToken ct = default)
    {
        var name = (displayName ?? string.Empty).Trim();
        if (!IsValidName(name, out var error))
        {
            return FolderOperationResult.Fail(FolderOperationStatus.InvalidName, error);
        }

        try
        {
            var created = await _admin
                .CreateFolderAsync(accountId, parent?.FullName, name, ct)
                .ConfigureAwait(false);
            await _folders.UpsertAsync(created, ct).ConfigureAwait(false);
            return FolderOperationResult.Ok(created);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "创建文件夹失败：账户 {AccountId} 名称 {Name}", accountId, name);
            return FolderOperationResult.Fail(FolderOperationStatus.RemoteFailed, ex.Message);
        }
    }

    public async Task<FolderOperationResult> RenameAsync(
        long accountId, MailFolder folder, string newDisplayName, CancellationToken ct = default)
    {
        var name = (newDisplayName ?? string.Empty).Trim();
        if (!IsValidName(name, out var error))
        {
            return FolderOperationResult.Fail(FolderOperationStatus.InvalidName, error);
        }

        if (folder.Kind != FolderKind.None)
        {
            return FolderOperationResult.Fail(FolderOperationStatus.SystemFolderProtected, "系统文件夹不能重命名。");
        }

        try
        {
            var newFullName = await _admin
                .RenameFolderAsync(accountId, folder.FullName, name, ct)
                .ConfigureAwait(false);

            var updated = new MailFolder
            {
                AccountId = accountId,
                FullName = newFullName,
                DisplayName = name,
                Kind = folder.Kind,
                UidValidity = folder.UidValidity,
                LastMaxUid = folder.LastMaxUid,
                LastSyncUtc = folder.LastSyncUtc,
                UnreadCount = folder.UnreadCount,
                TotalCount = folder.TotalCount,
                IsSelectable = folder.IsSelectable
            };

            await _folders.DeleteAsync(folder.Id, ct).ConfigureAwait(false);
            await _folders.UpsertAsync(updated, ct).ConfigureAwait(false);
            return FolderOperationResult.Ok(updated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FolderOperationResult.Fail(FolderOperationStatus.RemoteFailed, ex.Message);
        }
    }

    public async Task<FolderOperationResult> DeleteAsync(MailFolder folder, CancellationToken ct = default)
    {
        if (folder.Kind != FolderKind.None)
        {
            return FolderOperationResult.Fail(FolderOperationStatus.SystemFolderProtected, "系统文件夹不能删除。");
        }

        if (await _messages.CountAsync(folderId: folder.Id, ct: ct).ConfigureAwait(false) > 0)
        {
            return FolderOperationResult.Fail(FolderOperationStatus.NotEmpty, "文件夹中仍有邮件，请先移动或删除其中的邮件。");
        }

        try
        {
            await _admin.DeleteFolderAsync(folder.AccountId, folder.FullName, ct).ConfigureAwait(false);
            await _folders.DeleteAsync(folder.Id, ct).ConfigureAwait(false);
            return FolderOperationResult.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FolderOperationResult.Fail(FolderOperationStatus.RemoteFailed, ex.Message);
        }
    }

    private static bool IsValidName(string name, out string error)
    {
        if (name.Length == 0)
        {
            error = "文件夹名称不能为空。";
            return false;
        }

        if (name.Length > 64)
        {
            error = "文件夹名称不能超过 64 个字符。";
            return false;
        }

        if (name.IndexOfAny(InvalidNameCharacters) >= 0 || name.Any(char.IsControl))
        {
            error = "文件夹名称不能包含 / \\ : * ? \" < > | 等字符。";
            return false;
        }

        if (name is "." or "..")
        {
            error = "文件夹名称不合法。";
            return false;
        }

        error = "";
        return true;
    }
}
