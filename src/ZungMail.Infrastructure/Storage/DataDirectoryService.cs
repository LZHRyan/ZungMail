using System.Globalization;
using ZungMail.Core.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// 数据目录切换：只写引导指针，迁移在下次启动、任何数据库连接打开之前执行。
/// 迁移是「复制 + 校验 + 修正库内绝对路径 + 切指针」，旧目录一律保留作为备份。
/// </summary>
public sealed class DataDirectoryService : IDataDirectoryService
{
    /// <summary>目标盘需要预留的空间系数（文件系统开销、日志增长）。</summary>
    private const double RequiredFactor = 1.2;

    /// <summary>额外的安全余量：32 MB。</summary>
    private const long RequiredSlackBytes = 32L * 1024 * 1024;

    private const string DatabaseFileName = "mail.db";

    /// <summary>迁移时不复制的文件：指针文件与临时文件。</summary>
    private static readonly string[] SkipFileNames = { "location.json", "location.json.tmp" };

    private readonly DataDirectoryPointer _pointer;
    private readonly ILogger<DataDirectoryService> _logger;

    public DataDirectoryService(
        DataDirectoryPointer? pointer = null,
        ILogger<DataDirectoryService>? logger = null)
    {
        _pointer = pointer ?? new DataDirectoryPointer();
        _logger = logger ?? NullLogger<DataDirectoryService>.Instance;
    }

    // ---------------- 状态 ----------------

    public DataDirectoryStatus GetStatus()
    {
        var state = _pointer.Read();
        var pending = state.PendingSource;

        return new DataDirectoryStatus(
            _pointer.DefaultDirectory,
            pending ?? state.DataDirectory,
            pending is null ? null : state.DataDirectory,
            pending);
    }

    public void ScheduleMigration(string targetDirectory)
    {
        var target = DataDirectoryPointer.Normalize(targetDirectory)
                     ?? throw new ArgumentException("数据目录必须是绝对路径", nameof(targetDirectory));

        var effective = GetStatus().CurrentDirectory;
        if (string.Equals(target, effective, StringComparison.OrdinalIgnoreCase))
        {
            // 选的还是当前目录：等价于取消待迁移
            _pointer.Write(new DataDirectoryState { DataDirectory = effective });
            return;
        }

        _pointer.Write(new DataDirectoryState
        {
            DataDirectory = target,
            PendingSource = effective
        });
    }

    public void CancelPending()
    {
        var effective = GetStatus().CurrentDirectory;
        _pointer.Write(new DataDirectoryState { DataDirectory = effective });
    }

    // ---------------- 预检 ----------------

    public async Task<DataDirectoryValidation> ValidateTargetAsync(
        string targetDirectory,
        CancellationToken ct = default)
    {
        var target = DataDirectoryPointer.Normalize(targetDirectory);
        if (target is null) return Fail("请选择一个绝对路径的文件夹。");

        var effective = GetStatus().CurrentDirectory;
        if (string.Equals(target, effective, StringComparison.OrdinalIgnoreCase))
            return Fail("这就是当前正在使用的数据目录。");

        if (IsNetworkPath(target))
            return Fail("不支持网络位置（共享目录或映射盘）。SQLite 的 WAL 依赖本地文件锁，放在网络盘上容易损坏数据库。");

        if (IsInside(target, effective))
            return Fail("目标目录不能位于当前数据目录内部。");

        if (IsInside(effective, target))
            return Fail("目标目录不能是当前数据目录的上级目录。");

        // 写权限：真正建一次目录并写一个探针文件
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, ".zungmail-write-test");
            await File.WriteAllTextAsync(probe, "ok", ct).ConfigureAwait(false);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "数据目录不可写：{Target}", target);
            return Fail("这个目录没有写入权限，请换一个位置（Program Files、Windows 目录等受保护路径不能作为数据目录）。");
        }

        var sourceBytes = await Task.Run(() => DirectorySize(effective, ct), ct).ConfigureAwait(false);
        var required = (long)(sourceBytes * RequiredFactor) + RequiredSlackBytes;

        long freeBytes = 0;
        try
        {
            var root = Path.GetPathRoot(target);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady) freeBytes = drive.AvailableFreeSpace;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取目标盘剩余空间失败：{Target}", target);
        }

        if (freeBytes > 0 && freeBytes < required)
        {
            return Fail(
                $"目标盘空间不足：需要约 {FormatBytes(required)}，当前可用 {FormatBytes(freeBytes)}。",
                sourceBytes, required, freeBytes);
        }

        var willBackup = false;
        var willCleanLeftovers = false;
        if (Directory.Exists(target) && HasMeaningfulEntry(target))
        {
            if (!LooksLikeDataDirectory(target))
            {
                return Fail(
                    "目标目录不是空的，也不是 ZungMail 的数据目录。请选择一个空文件夹。",
                    sourceBytes, required, freeBytes);
            }

            willBackup = true;
        }
        else if (Directory.Exists(target) && ContainsAnyEntry(target))
        {
            // 只剩上次失败留下的日志 / 头像缓存 / WebView2 目录：清掉即可，不当成「已有数据」
            willCleanLeftovers = true;
        }

        var message = willBackup
            ? "目标目录里已有一份 ZungMail 数据，迁移前会自动改名备份保留。"
            : willCleanLeftovers
                ? "目标目录可用：迁移前会先清掉上次留下的日志与缓存。"
                : "目标目录可用。";

        return new DataDirectoryValidation(true, message, sourceBytes, required, freeBytes, willBackup);
    }

    // ---------------- 迁移 ----------------

    public async Task<DataMigrationResult> MigratePendingAsync(
        IProgress<DataMigrationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var state = _pointer.Read();
        if (string.IsNullOrWhiteSpace(state.PendingSource))
        {
            return new DataMigrationResult(true, "没有待迁移的数据目录。", state.DataDirectory);
        }

        var source = state.PendingSource;
        var target = state.DataDirectory;

        try
        {
            return await MigrateAsync(source, target, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("数据目录迁移被取消：{Source} → {Target}", source, target);
            return new DataMigrationResult(false, "迁移已取消，数据目录保持不变。", source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据目录迁移失败：{Source} → {Target}", source, target);
            return new DataMigrationResult(false, "迁移失败：" + ex.Message, source);
        }
    }

    private async Task<DataMigrationResult> MigrateAsync(
        string source,
        string target,
        IProgress<DataMigrationProgress>? progress,
        CancellationToken ct)
    {
        if (!Directory.Exists(source))
        {
            // 源目录不存在（被手工删了）：没有可迁移的数据，直接把指针切过去。
            _pointer.Write(new DataDirectoryState { DataDirectory = target });
            return new DataMigrationResult(true, "原数据目录已不存在，已直接切换到新目录。", target);
        }

        progress?.Report(new DataMigrationProgress("预检", 2, "检查目标目录…"));
        var validation = await ValidateTargetAsync(target, ct).ConfigureAwait(false);
        if (!validation.Ok)
        {
            // 预检失败时不改指针：下次启动仍用旧目录，用户可以重新选择。
            return new DataMigrationResult(false, validation.Message, source);
        }

        string? backupPath = null;
        var targetExisted = Directory.Exists(target);
        var copyStarted = false;

        try
        {
            if (Directory.Exists(target) && HasMeaningfulEntry(target))
            {
                backupPath = target + "_backup_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                progress?.Report(new DataMigrationProgress("预检", 6, "备份目标目录里的原有数据…"));
                Directory.Move(target, backupPath);
                _logger.LogInformation("目标目录已有数据，已备份为 {Backup}", backupPath);
            }

            Directory.CreateDirectory(target);
            CleanupLeftovers(target);

            // 1) 整理源库：把 WAL 落盘，这样复制 mail.db 单文件就是一致的
            progress?.Report(new DataMigrationProgress("整理数据库", 10, "把未落盘的日志写回数据库…"));
            var counts = await CheckpointAndCountAsync(source, ct).ConfigureAwait(false);

            // 连接池会保持源库文件句柄：复制前清池，确保 mail.db / -wal / -shm 都能被独占读取
            SqliteConnection.ClearAllPools();

            // 2) 复制文件
            copyStarted = true;
            await CopyTreeAsync(source, target, progress, ct).ConfigureAwait(false);

            // 3) 校验
            progress?.Report(new DataMigrationProgress("校验", 84, "核对文件与数据库…"));
            var targetCounts = await CountAsync(target, ct).ConfigureAwait(false);
            if (counts.Total > 0 && !counts.Equals(targetCounts))
            {
                throw new InvalidOperationException(
                    $"数据校验失败：源库邮件 {counts.Messages} 封 / 附件 {counts.Attachments} 条，复制后为 {targetCounts.Messages} 封 / {targetCounts.Attachments} 条。");
            }

            // 4) 修正库内绝对路径
            progress?.Report(new DataMigrationProgress("修正路径", 92, "把附件与内嵌图片的路径指向新目录…"));
            var rewritten = await RewritePathsAsync(target, source, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "数据目录迁移：复制完成，重写路径 {Attachments} 条附件、{Bodies} 封正文、{Eml} 条 eml 记录",
                rewritten.Attachments, rewritten.Bodies, rewritten.EmlPaths);

            // 5) 切指针（最后一步，失败则不生效）
            progress?.Report(new DataMigrationProgress("完成", 99, "切换数据目录…"));
            _pointer.Write(new DataDirectoryState { DataDirectory = target });

            progress?.Report(new DataMigrationProgress("完成", 100, "迁移完成"));
            return new DataMigrationResult(true, "数据已迁移到新目录，旧目录保留作为备份。", target);
        }
        catch (Exception)
        {
            // 回滚：删掉目标里的半成品，把备份改回来；指针不动（下次启动仍用旧目录）
            TryRollback(target, backupPath, targetExisted, copyStarted);
            throw;
        }
    }

    private void TryRollback(string target, string? backupPath, bool targetExisted, bool copyStarted)
    {
        try
        {
            if (copyStarted && Directory.Exists(target)) Directory.Delete(target, recursive: true);
            if (backupPath is not null && Directory.Exists(backupPath)) Directory.Move(backupPath, target);
            else if (!targetExisted && Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "迁移回滚时清理目标目录失败：{Target}", target);
        }
    }

    // ---------------- 复制 ----------------

    private static async Task CopyTreeAsync(
        string source,
        string target,
        IProgress<DataMigrationProgress>? progress,
        CancellationToken ct)
    {
        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (SkipFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            files.Add(new FileInfo(path));
        }

        var totalBytes = files.Sum(f => f.Length);
        long copied = 0;
        var lastReport = DateTime.UtcNow;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file.FullName);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using (var input = new FileStream(
                             file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, true))
            await using (var output = new FileStream(
                             destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;

                    if (DateTime.UtcNow - lastReport < TimeSpan.FromMilliseconds(120)) continue;
                    lastReport = DateTime.UtcNow;
                    var percent = totalBytes == 0 ? 60 : 12 + (int)(copied * 68 / totalBytes);
                    progress?.Report(new DataMigrationProgress(
                        "复制文件",
                        Math.Clamp(percent, 12, 80),
                        $"{FormatBytes(copied)} / {FormatBytes(totalBytes)} · {relative}"));
                }
            }

            try { File.SetLastWriteTimeUtc(destination, file.LastWriteTimeUtc); }
            catch (IOException) { /* 时间戳失败不影响数据 */ }
        }
    }

    // ---------------- 数据库 ----------------

    private readonly record struct TableCounts(int Messages, int Attachments, int Folders)
    {
        public int Total => Messages + Attachments + Folders;
    }

    private static async Task<TableCounts> CheckpointAndCountAsync(string root, CancellationToken ct)
    {
        var database = Path.Combine(root, DatabaseFileName);
        if (!File.Exists(database)) return default;

        await using var connection = Open(database);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return await ReadCountsAsync(connection, ct).ConfigureAwait(false);
    }

    private static async Task<TableCounts> CountAsync(string root, CancellationToken ct)
    {
        var database = Path.Combine(root, DatabaseFileName);
        if (!File.Exists(database)) return default;

        await using var connection = Open(database);
        return await ReadCountsAsync(connection, ct).ConfigureAwait(false);
    }

    private static async Task<TableCounts> ReadCountsAsync(SqliteConnection connection, CancellationToken ct)
    {
        async Task<int> CountRowsAsync(string table)
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM {table};";
                var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return Convert.ToInt32(value ?? 0, CultureInfo.InvariantCulture);
            }
            catch (SqliteException)
            {
                // 表不存在（旧库或半初始化）时按 0 处理
                return 0;
            }
        }

        return new TableCounts(
            await CountRowsAsync("messages").ConfigureAwait(false),
            await CountRowsAsync("attachments").ConfigureAwait(false),
            await CountRowsAsync("folders").ConfigureAwait(false));
    }

    private readonly record struct RewriteCounts(int Attachments, int Bodies, int EmlPaths);

    /// <summary>
    /// 整个数据根目录被改名（旧品牌 → 新品牌）之后，修正库里的绝对路径：
    /// 附件 local_path、messages.eml_path、以及正文里的 <c>file://</c> 前缀。
    /// <para>
    /// 与「用户主动换目录」不同，改名是不复制文件的 rename，路径必须跟着改，
    /// 否则 700 MB 的库搬过去后所有附件都会显示「未下载」、内嵌图片全部变成占位。
    /// 同步调用，因为调用点（启动早期读指针）本身就是同步流程；失败由调用方降级。
    /// </para>
    /// </summary>
    internal static void RewriteRootPaths(string oldRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(oldRoot) || string.IsNullOrWhiteSpace(newRoot)) return;
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(oldRoot),
                Path.TrimEndingDirectorySeparator(newRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!File.Exists(Path.Combine(newRoot, DatabaseFileName))) return;

        RewritePathsAsync(newRoot, oldRoot, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>把库里的绝对路径从旧目录前缀改成新目录前缀（附件、eml、内嵌图片的 file:// URI）。</summary>
    private static async Task<RewriteCounts> RewritePathsAsync(string target, string source, CancellationToken ct)
    {
        var database = Path.Combine(target, DatabaseFileName);
        if (!File.Exists(database)) return default;

        var oldEml = Path.Combine(source, "eml");
        var newEml = Path.Combine(target, "eml");
        var oldPrefix = oldEml + Path.DirectorySeparatorChar;
        var newPrefix = newEml + Path.DirectorySeparatorChar;
        var oldUri = DirectoryUriPrefix(oldEml);
        var newUri = DirectoryUriPrefix(newEml);

        await using var connection = Open(database);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var attachments = await RewriteColumnAsync(
            connection, tx, "attachments", "local_path", oldPrefix, newPrefix, ct)
            .ConfigureAwait(false);

        var emlPaths = await RewriteColumnAsync(
            connection, tx, "messages", "eml_path", oldPrefix, newPrefix, ct)
            .ConfigureAwait(false);

        var bodies = 0;
        if (oldUri is not null && newUri is not null &&
            !string.Equals(oldUri, newUri, StringComparison.Ordinal))
        {
            var updates = new List<(long Id, string Html)>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText =
                    "SELECT id, body_html FROM messages WHERE body_html IS NOT NULL AND instr(body_html, @old) > 0;";
                select.Parameters.AddWithValue("@old", oldUri);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    updates.Add((reader.GetInt64(0), reader.GetString(1)));
                }
            }

            foreach (var (id, html) in updates)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = tx;
                update.CommandText = "UPDATE messages SET body_html = @html WHERE id = @id;";
                update.Parameters.AddWithValue("@html", html.Replace(oldUri, newUri, StringComparison.Ordinal));
                update.Parameters.AddWithValue("@id", id);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            bodies = updates.Count;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new RewriteCounts(attachments, bodies, emlPaths);
    }

    private static async Task<int> RewriteColumnAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string table,
        string column,
        string oldPrefix,
        string newPrefix,
        CancellationToken ct)
    {
        if (string.Equals(oldPrefix, newPrefix, StringComparison.Ordinal)) return 0;

        var updates = new List<(long Id, string Value)>();
        try
        {
            await using var select = connection.CreateCommand();
            select.Transaction = tx;
            select.CommandText =
                $"SELECT id, {column} FROM {table} WHERE {column} IS NOT NULL AND instr({column}, @old) = 1;";
            select.Parameters.AddWithValue("@old", oldPrefix);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                updates.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }
        catch (SqliteException)
        {
            // 列不存在（历史结构）时跳过
            return 0;
        }

        foreach (var (id, value) in updates)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = $"UPDATE {table} SET {column} = @value WHERE id = @id;";
            update.Parameters.AddWithValue("@value", newPrefix + value[oldPrefix.Length..]);
            update.Parameters.AddWithValue("@id", id);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return updates.Count;
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>
    /// 目录的 file:// URI 前缀（含百分号编码），与 MimeMapper 写入正文时的形式一致。
    /// 用一个代表性文件名生成再截掉，保证编码规则与 <see cref="Uri"/> 完全相同。
    /// </summary>
    private static string? DirectoryUriPrefix(string directory)
    {
        try
        {
            var sample = Path.Combine(directory, "x");
            var uri = new Uri(sample).AbsoluteUri;
            return uri.Length > 1 ? uri[..^1] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------- 工具 ----------------

    private static DataDirectoryValidation Fail(
        string message,
        long sourceBytes = 0,
        long requiredBytes = 0,
        long freeBytes = 0)
        => new(false, message, sourceBytes, requiredBytes, freeBytes);

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            if (new Uri(path).IsUnc) return true;
        }
        catch (UriFormatException)
        {
            // 交给后面的写权限检查
        }

        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsInside(string child, string parent)
    {
        var prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAnyEntry(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// 程序自己生成的、迁移时可以直接丢掉的目录名：日志、头像缓存、WebView2 用户数据。
    /// 上一次迁移尝试失败后客户端会把它们写进目标目录，不能因此认定「目标不是空目录」。
    /// </summary>
    private static readonly string[] IgnorableEntries = { "logs", "avatars", "WebView2Preview", ".zungmail-write-test" };

    private static bool IsIgnorableEntry(string name)
        => IgnorableEntries.Any(entry => string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))
           || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>除程序自建产物外，目标目录里还有没有别的东西（有才需要备份或拒绝）。</summary>
    private static bool HasMeaningfulEntry(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Any(path => !IsIgnorableEntry(Path.GetFileName(path)));
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>迁移前清掉目标目录里的遗留日志 / 缓存，避免和源目录的同名目录混在一起。</summary>
    private void CleanupLeftovers(string target)
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(target))
            {
                var name = Path.GetFileName(path);
                if (!IsIgnorableEntry(name)) continue;

                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else File.Delete(path);
                    _logger.LogInformation("迁移前清掉目标目录里的遗留物：{Path}", path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清掉目标目录遗留物失败：{Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扫描目标目录遗留物失败：{Target}", target);
        }
    }

    private static bool LooksLikeDataDirectory(string directory)
        => File.Exists(Path.Combine(directory, DatabaseFileName))
           || File.Exists(Path.Combine(directory, "settings.json"))
           || File.Exists(Path.Combine(directory, "templates.json"));

    private static long DirectorySize(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return 0;

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try { total += new FileInfo(file).Length; }
                catch (IOException) { /* 正在写入的文件忽略 */ }
            }
        }
        catch (Exception)
        {
            // 统计失败按 0 处理：预检只是估算
        }

        return total;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1024L * 1024 => (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes + " B"
    };
}
