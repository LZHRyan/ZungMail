namespace ZungMail.Core.Abstractions;

/// <summary>本地邮件导入（.eml / .msg 解析后的 RFC 822 流）结果。</summary>
public sealed record MailImportResult(int Imported, int Duplicates, IReadOnlyList<string> Failures)
{
    public bool Success => Failures.Count == 0;

    public string Summary => Failures.Count == 0
        ? $"导入完成：新增 {Imported} 封，跳过重复 {Duplicates} 封"
        : $"导入结束：新增 {Imported} 封，跳过重复 {Duplicates} 封，失败 {Failures.Count} 个文件";
}

/// <summary>
/// 把外部 .eml 文件导入到指定账户的文件夹。用于从 Foxmail / Outlook 等客户端迁移历史邮件；
/// 以 Message-ID 去重，重复导入同一文件不会产生副本。
/// </summary>
public interface IMailImportService
{
    Task<MailImportResult> ImportAsync(
        long accountId,
        long folderId,
        IReadOnlyList<string> files,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}
