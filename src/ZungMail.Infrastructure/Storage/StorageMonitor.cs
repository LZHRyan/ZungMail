using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// 本地存储监控。使用 SQL 聚合统计邮件与附件占用、按文件元数据统计数据库/日志大小，
/// 不做附件目录递归扫描，因此在百万级邮件下依然可以在毫秒级返回。
/// </summary>
public sealed class StorageMonitor : IStorageMonitor
{
    private readonly IAppPaths _paths;
    private readonly ISettingsStore _settings;
    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<StorageMonitor> _logger;

    public StorageMonitor(
        IAppPaths paths,
        ISettingsStore settings,
        SqliteConnectionFactory factory,
        ILogger<StorageMonitor> logger)
    {
        _paths = paths;
        _settings = settings;
        _factory = factory;
        _logger = logger;
    }

    public async Task<StorageSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        long attachmentBytes = 0;
        int messageCount = 0;
        int attachmentCount = 0;
        long reclaimable = 0;

        try
        {
            await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
SELECT COUNT(*),
       COALESCE(SUM(CASE WHEN a.is_inline = 0 AND a.local_path IS NOT NULL THEN a.size ELSE 0 END), 0),
       COALESCE(SUM(CASE WHEN a.is_inline = 0 AND a.local_path IS NOT NULL THEN 1 ELSE 0 END), 0)
FROM attachments a;
""";
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    attachmentCount = reader.GetInt32(2);
                    attachmentBytes = reader.GetInt64(1);
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM messages;";
                messageCount = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
            }

            await using (var command = connection.CreateCommand())
            {
                // 可释放估算：垃圾箱 + 广告邮件的邮件本身与附件文件
                command.CommandText = """
SELECT COALESCE(SUM(m.size), 0)
       + COALESCE((SELECT SUM(a.size)
                   FROM attachments a
                   JOIN messages m2 ON m2.id = a.message_id
                   JOIN folders f2 ON f2.id = m2.folder_id
                   WHERE f2.kind IN (4, 5) AND a.is_inline = 0 AND a.local_path IS NOT NULL), 0)
FROM messages m
JOIN folders f ON f.id = m.folder_id
WHERE f.kind IN (4, 5);
""";
                reclaimable = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            }
        }
        catch (Exception ex)
        {
            // 监控失败不应影响主流程
            _logger.LogWarning(ex, "统计邮件存储占用失败");
        }

        var databaseBytes = FileSize(_paths.DatabasePath)
            + FileSize(_paths.DatabasePath + "-wal")
            + FileSize(_paths.DatabasePath + "-shm");
        var logBytes = DirectorySize(_paths.LogDirectory);

        long freeBytes = 0;
        long driveTotal = 0;
        try
        {
            var root = Path.GetPathRoot(_paths.RootDirectory);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    freeBytes = drive.AvailableFreeSpace;
                    driveTotal = drive.TotalSize;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取磁盘剩余空间失败");
        }

        var settings = _settings.Current;
        var totalBytes = databaseBytes + attachmentBytes;
        var usage = settings.StorageLimitBytes > 0
            ? totalBytes * 100.0 / settings.StorageLimitBytes
            : driveTotal > 0
                ? (driveTotal - freeBytes) * 100.0 / driveTotal
                : 0;
        usage = Math.Clamp(usage, 0, 100);

        var freePercent = driveTotal > 0 ? freeBytes * 100.0 / driveTotal : 100;
        var level = Evaluate(settings, usage, out var alertText);

        return new StorageSnapshot
        {
            DatabaseBytes = databaseBytes,
            AttachmentBytes = attachmentBytes,
            LogBytes = logBytes,
            FreeBytes = freeBytes,
            DriveTotalBytes = driveTotal,
            ReclaimableBytes = reclaimable,
            MessageCount = messageCount,
            AttachmentCount = attachmentCount,
            UsagePercent = Math.Round(usage, 1),
            FreePercent = Math.Round(freePercent, 1),
            Level = level,
            AlertText = alertText
        };
    }

    private static StorageAlertLevel Evaluate(AppSettings settings, double usage, out string text)
    {
        text = string.Empty;
        if (!settings.StorageAlertsEnabled) return StorageAlertLevel.Normal;

        var notice = Math.Max(1, settings.StorageNoticePercent);
        var warning = Math.Max(notice, settings.StorageWarningPercent);
        var critical = Math.Max(warning, settings.StorageCriticalPercent);

        if (usage >= critical)
        {
            text = $"存储占用 {usage:0.#}%，空间即将耗尽，请立即清理历史邮件";
            return StorageAlertLevel.Critical;
        }
        if (usage >= warning)
        {
            text = $"存储占用 {usage:0.#}%，建议立即清理以释放空间";
            return StorageAlertLevel.Warning;
        }
        if (usage >= notice)
        {
            text = $"存储占用 {usage:0.#}%，可考虑清理垃圾箱或广告邮件";
            return StorageAlertLevel.Notice;
        }
        return StorageAlertLevel.Normal;
    }

    private static long FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { /* 正在写入的日志文件忽略 */ }
            }
            return total;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
