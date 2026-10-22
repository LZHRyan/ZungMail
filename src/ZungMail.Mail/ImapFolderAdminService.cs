using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.Mail;

/// <summary>
/// 基于现有 IMAP 会话工厂的文件夹管理实现。由 DI 单例持有，避免把管理命令塞进同步引擎。
/// </summary>
public sealed class ImapFolderAdminService : IMailFolderAdminService
{
    /// <summary>部分服务商要求在固定容器下创建自定义文件夹（QQ 邮箱等）。</summary>
    private static readonly string[] ContainerNames = { "其他文件夹", "Other Folders", "其他文件夾" };

    private readonly IAccountRepository _accounts;
    private readonly IImapClientFactory _clientFactory;
    private readonly ILogger<ImapFolderAdminService> _logger;

    public ImapFolderAdminService(
        IAccountRepository accounts,
        IImapClientFactory clientFactory,
        ILogger<ImapFolderAdminService> logger)
    {
        _accounts = accounts;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public async Task<MailFolder> CreateFolderAsync(
        long accountId, string? parentFullName, string displayName, CancellationToken ct = default)
    {
        var account = await RequireAccountAsync(accountId, ct).ConfigureAwait(false);
        using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);

        IImapFolderSession created;
        if (string.IsNullOrWhiteSpace(parentFullName))
        {
            created = await CreateAtRootAsync(client, displayName, ct).ConfigureAwait(false);
        }
        else
        {
            var parent = client.TryGetFolder(parentFullName)
                ?? throw new InvalidOperationException($"父文件夹不存在：{parentFullName}");
            created = await parent.CreateChildAsync(displayName, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "已在账户 {Account} 创建文件夹 {Folder}（{Kind}）", account.Email, created.FullName, created.Attributes);

        return new MailFolder
        {
            AccountId = accountId,
            FullName = created.FullName,
            DisplayName = created.Name,
            Kind = MailkitMapping.InferKind(created.FullName, created.Name, created.Attributes),
            IsSelectable = (created.Attributes & MailKit.FolderAttributes.NoSelect) == 0
        };
    }

    /// <summary>
    /// 先在个人命名空间根创建；若服务商拒绝，再尝试其约定的自定义文件夹容器。
    /// 这样既兼容标准服务器，也能覆盖 QQ 邮箱这类只允许在「其他文件夹」下创建的限制。
    /// </summary>
    private async Task<IImapFolderSession> CreateAtRootAsync(
        IImapSession client, string displayName, CancellationToken ct)
    {
        var root = await client.GetRootFolderAsync(ct).ConfigureAwait(false);
        try
        {
            return await root.CreateChildAsync(displayName, ct).ConfigureAwait(false);
        }
        catch (Exception rootError) when (rootError is not OperationCanceledException)
        {
            var container = await FindContainerAsync(client, ct).ConfigureAwait(false);
            if (container is null)
            {
                _logger.LogWarning(rootError, "在根命名空间创建文件夹 {Folder} 失败", displayName);
                throw;
            }

            try
            {
                var created = await container.CreateChildAsync(displayName, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "根级创建被拒绝，已改在容器 {Container} 下创建 {Folder}", container.FullName, created.FullName);
                return created;
            }
            catch (Exception nestedError) when (nestedError is not OperationCanceledException)
            {
                _logger.LogWarning(nestedError,
                    "在容器 {Container} 下创建文件夹 {Folder} 仍失败（根级错误：{RootError}）",
                    container.FullName, displayName, rootError.Message);
                throw new InvalidOperationException(
                    $"服务器拒绝了创建请求：{nestedError.Message}", nestedError);
            }
        }
    }

    private static async Task<IImapFolderSession?> FindContainerAsync(IImapSession client, CancellationToken ct)
    {
        IReadOnlyList<IImapFolderSession> folders;
        try
        {
            folders = await client.GetFoldersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }

        return folders.FirstOrDefault(folder =>
            ContainerNames.Any(name =>
                folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                folder.FullName.EndsWith(name, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<string> RenameFolderAsync(
        long accountId, string fullName, string newDisplayName, CancellationToken ct = default)
    {
        var account = await RequireAccountAsync(accountId, ct).ConfigureAwait(false);
        using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
        return await client.RenameFolderAsync(fullName, newDisplayName, ct).ConfigureAwait(false);
    }

    public async Task DeleteFolderAsync(long accountId, string fullName, CancellationToken ct = default)
    {
        var account = await RequireAccountAsync(accountId, ct).ConfigureAwait(false);
        using var client = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
        var folder = client.TryGetFolder(fullName)
            ?? throw new InvalidOperationException($"文件夹不存在：{fullName}");
        await folder.DeleteAsync(ct).ConfigureAwait(false);
    }

    private async Task<Account> RequireAccountAsync(long accountId, CancellationToken ct)
        => await _accounts.GetAsync(accountId, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException("账户不存在或已被删除。");
}
