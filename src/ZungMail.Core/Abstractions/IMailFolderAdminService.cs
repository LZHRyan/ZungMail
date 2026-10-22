using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>
/// 服务器端文件夹管理端口。由邮件协议层实现，Application 层只依赖本接口，
/// 从而可以在不直接引用 MailKit 的前提下创建、重命名及删除 IMAP 文件夹。
/// </summary>
public interface IMailFolderAdminService
{
    /// <summary>在指定父文件夹下创建文件夹；<paramref name="parentFullName"/> 为空表示根级。</summary>
    Task<MailFolder> CreateFolderAsync(
        long accountId, string? parentFullName, string displayName, CancellationToken ct = default);

    /// <summary>重命名普通文件夹，返回新的完整 IMAP 路径。</summary>
    Task<string> RenameFolderAsync(
        long accountId, string fullName, string newDisplayName, CancellationToken ct = default);

    /// <summary>删除空文件夹。</summary>
    Task DeleteFolderAsync(long accountId, string fullName, CancellationToken ct = default);
}
