using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>应用设置持久化（JSON）。</summary>
public interface ISettingsStore
{
    /// <summary>当前生效设置（只读使用；修改后调用 <see cref="SaveAsync"/>）。</summary>
    AppSettings Current { get; }

    /// <summary>
    /// 最近一次加载因文件损坏而回退默认值时的提示（含备份文件名）；正常加载为 null。
    /// </summary>
    string? LoadWarning { get; }

    Task LoadAsync(CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);
}

/// <summary>邮件模板持久化（JSON 文件，FR-08）。</summary>
public interface ITemplateStore
{
    /// <summary>
    /// 最近一次读取因文件损坏而回退内置模板时的提示（含备份文件名）；正常读取为 null。
    /// </summary>
    string? LoadWarning { get; }

    Task<IReadOnlyList<MailTemplate>> GetAllAsync(CancellationToken ct = default);

    Task SaveAsync(IReadOnlyList<MailTemplate> templates, CancellationToken ct = default);
}

/// <summary>凭据保护（NFR-S2）。实现需保证同一用户/机器可还原。</summary>
public interface ICredentialProtector
{
    /// <summary>明文 → 密文（Base64）。</summary>
    string Protect(string plainText);

    /// <summary>密文 → 明文；失败时抛出 <see cref="System.Security.Cryptography.CryptographicException"/>。</summary>
    string Unprotect(string cipherText);
}

/// <summary>服务器配置自动匹配（FR-02）。</summary>
public interface IServerConfigResolver
{
    /// <summary>按邮箱域名解析预置配置，未命中返回 null（界面转为手动配置）。</summary>
    ServerPreset? Resolve(string email);

    IReadOnlyList<ServerPreset> All { get; }
}

/// <summary>内置服务器配置模板。</summary>
public sealed record ServerPreset(
    string Domain,
    string DisplayName,
    string ImapHost,
    int ImapPort,
    SecureMode ImapSecure,
    string SmtpHost,
    int SmtpPort,
    SecureMode SmtpSecure);

/// <summary>数据导出（FR-13）：打包 SQLite 与 .eml 目录为 zip。</summary>
public interface IDataExporter
{
    Task<string> ExportAsync(string targetZipPath, CancellationToken ct = default);
}

/// <summary>应用路径约定。</summary>
public interface IAppPaths
{
    string RootDirectory { get; }
    string DatabasePath { get; }
    string TemplatesPath { get; }
    string SettingsPath { get; }
    string LogDirectory { get; }
    string EmlDirectory { get; }

    /// <summary>确保所有目录存在。</summary>
    void EnsureCreated();
}
