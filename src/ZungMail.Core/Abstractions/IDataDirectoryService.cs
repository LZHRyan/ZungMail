namespace ZungMail.Core.Abstractions;

/// <summary>数据目录状态：当前生效目录、默认目录，以及是否还有一次待执行的迁移。</summary>
public sealed record DataDirectoryStatus(
    string DefaultDirectory,
    string CurrentDirectory,
    string? PendingTarget,
    string? PendingSource)
{
    public bool IsCustom =>
        !string.Equals(
            Path.TrimEndingDirectorySeparator(CurrentDirectory),
            Path.TrimEndingDirectorySeparator(DefaultDirectory),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>已排入下次启动的迁移；为空表示没有待办。</summary>
    public bool HasPending => !string.IsNullOrWhiteSpace(PendingTarget);
}

/// <summary>切换数据目录的预检结果（只做检查，不改任何数据）。</summary>
public sealed record DataDirectoryValidation(
    bool Ok,
    string Message,
    long SourceBytes = 0,
    long RequiredBytes = 0,
    long FreeBytes = 0,
    bool WillBackupExisting = false);

/// <summary>迁移进度：阶段名 + 0~100 百分比 + 明细文案。</summary>
public sealed record DataMigrationProgress(string Stage, int Percent, string Detail);

/// <summary>迁移结果。</summary>
public sealed record DataMigrationResult(bool Success, string Message, string? Target = null);

/// <summary>
/// 数据保存目录：只负责「指针 + 预检 + 迁移」三件事。
/// 改目录只写一个引导指针（<c>%APPDATA%\ZungMail\location.json</c>），
/// 真正的复制在下次启动、任何数据库连接打开之前完成，避免运行时热切换。
/// </summary>
public interface IDataDirectoryService
{
    /// <summary>当前状态（默认目录、生效目录、待迁移目标）。</summary>
    DataDirectoryStatus GetStatus();

    /// <summary>预检目标目录：是否是本地盘、可写、空间够、是否已有数据需要备份。</summary>
    Task<DataDirectoryValidation> ValidateTargetAsync(string targetDirectory, CancellationToken ct = default);

    /// <summary>只写指针：下次启动时把数据迁移到 <paramref name="targetDirectory"/>。</summary>
    void ScheduleMigration(string targetDirectory);

    /// <summary>取消尚未执行的迁移，指针回到当前目录。</summary>
    void CancelPending();

    /// <summary>
    /// 执行待迁移（启动阶段调用）。复制 → 校验 → 修正库内绝对路径 → 写指针；
    /// 任何一步失败都不改动指针，旧目录保持可用。
    /// </summary>
    Task<DataMigrationResult> MigratePendingAsync(
        IProgress<DataMigrationProgress>? progress = null,
        CancellationToken ct = default);
}
