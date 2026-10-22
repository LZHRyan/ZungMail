namespace ZungMail.Core.Domain;

/// <summary>磁盘空间预警级别（按占用比例分级）。</summary>
public enum StorageAlertLevel
{
    /// <summary>正常。</summary>
    Normal = 0,

    /// <summary>提示：接近阈值，可考虑清理。</summary>
    Notice = 1,

    /// <summary>警告：建议立即清理。</summary>
    Warning = 2,

    /// <summary>严重：空间即将耗尽。</summary>
    Critical = 3
}

/// <summary>
/// 本地邮件存储快照：数据库（含正文）、附件文件、日志、磁盘剩余与可释放空间估算。
/// 全部数值来自轻量 SQL 聚合与文件元数据，不做递归目录扫描，保证百万级邮件下也能快速返回。
/// </summary>
public sealed class StorageSnapshot
{
    /// <summary>数据库及 WAL 文件占用。</summary>
    public long DatabaseBytes { get; init; }

    /// <summary>附件文件占用（按 attachments.size 聚合估算）。</summary>
    public long AttachmentBytes { get; init; }

    /// <summary>日志目录占用。</summary>
    public long LogBytes { get; init; }

    /// <summary>邮件数据总占用（数据库 + 附件）。</summary>
    public long TotalBytes => DatabaseBytes + AttachmentBytes;

    /// <summary>所在磁盘剩余空间。</summary>
    public long FreeBytes { get; init; }

    /// <summary>所在磁盘总容量。</summary>
    public long DriveTotalBytes { get; init; }

    /// <summary>预计可通过清理垃圾箱 / 广告邮件释放的字节数。</summary>
    public long ReclaimableBytes { get; init; }

    public int MessageCount { get; init; }

    public int AttachmentCount { get; init; }

    /// <summary>占用百分比：配置了上限时相对上限，否则相对磁盘总容量。</summary>
    public double UsagePercent { get; init; }

    public double FreePercent { get; init; }

    public StorageAlertLevel Level { get; init; }

    /// <summary>预警说明（正常时为空）。</summary>
    public string AlertText { get; init; } = "";

    public string TotalDisplay => DisplayFormat.FileSize(TotalBytes);

    public string DatabaseDisplay => DisplayFormat.FileSize(DatabaseBytes);

    public string AttachmentDisplay => DisplayFormat.FileSize(AttachmentBytes);

    public string LogDisplay => DisplayFormat.FileSize(LogBytes);

    public string FreeDisplay => DisplayFormat.FileSize(FreeBytes);

    public string DriveTotalDisplay => DisplayFormat.FileSize(DriveTotalBytes);

    public string ReclaimableDisplay => DisplayFormat.FileSize(ReclaimableBytes);

    public string UsageDisplay => $"{UsagePercent:0.#}%";

    public string MessageCountDisplay => $"{MessageCount:N0} 封";

    public bool HasAlert => Level != StorageAlertLevel.Normal;
}
