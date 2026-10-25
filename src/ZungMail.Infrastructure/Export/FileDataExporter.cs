using System.IO.Compression;
using ZungMail.Core.Abstractions;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace ZungMail.Infrastructure.Export;

/// <summary>数据导出（FR-13）：把 SQLite 库、设置/模板与 .eml 目录打包为 zip。</summary>
public sealed class FileDataExporter : IDataExporter
{
    private readonly IAppPaths _paths;
    private readonly SqliteConnectionFactory _factory;

    public FileDataExporter(IAppPaths paths, SqliteConnectionFactory factory)
    {
        _paths = paths;
        _factory = factory;
    }

    public async Task<string> ExportAsync(string targetZipPath, CancellationToken ct = default)
    {
        var target = Path.GetFullPath(targetZipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // 先 checkpoint：把 WAL 内容落盘，再复制文件，避免导出丢失未写入主库的数据。
        // 注：checkpoint 需要写权限，因此这里用可读写连接而非只读打开。
        await CheckpointAsync(ct).ConfigureAwait(false);

        // 连接池会保持 db 文件句柄：复制前先清池，保证三个文件都能被独占读取
        SqliteConnection.ClearAllPools();

        if (File.Exists(target)) File.Delete(target);

        using (var archive = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            var db = _paths.DatabasePath;
            AddIfExists(archive, db, "mail.db");
            AddIfExists(archive, db + "-wal", "mail.db-wal");
            AddIfExists(archive, db + "-shm", "mail.db-shm");
            AddIfExists(archive, _paths.SettingsPath, "settings.json");
            AddIfExists(archive, _paths.TemplatesPath, "templates.json");

            var emlDir = _paths.EmlDirectory;
            if (Directory.Exists(emlDir))
            {
                foreach (var file in Directory.EnumerateFiles(emlDir, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(emlDir, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, "eml/" + relative, CompressionLevel.Optimal);
                }
            }
        }

        return target;
    }

    private async Task CheckpointAsync(CancellationToken ct)
    {
        if (!File.Exists(_paths.DatabasePath)) return;
        try
        {
            await using var connection = await _factory.CreateAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(FULL);";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // checkpoint 失败不阻断导出，退化为复制 db + wal/shm 三个文件。
        }
    }

    private static void AddIfExists(ZipArchive archive, string sourcePath, string entryName)
    {
        if (File.Exists(sourcePath))
        {
            archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
        }
    }
}
