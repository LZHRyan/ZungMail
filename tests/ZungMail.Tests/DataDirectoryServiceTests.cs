using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 数据保存目录：引导指针读写、目标预检、以及「复制 + 校验 + 修正库内绝对路径 + 切指针」的迁移。
/// 旧目录在迁移后保持原样（保留备份）。
/// </summary>
[Trait("Category", "Integration")]
public sealed class DataDirectoryServiceTests : IDisposable
{
    private readonly string _root;

    public DataDirectoryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-datadir", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DefaultDirectory);
    }

    private string DefaultDirectory => Path.Combine(_root, "default");
    private string TargetDirectory => Path.Combine(_root, "moved");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DataDirectoryPointer CreatePointer() => new(DefaultDirectory);

    private DataDirectoryService CreateService() => new(CreatePointer());

    // ---------------- 指针 ----------------

    [Fact]
    public void Read_没有指针文件时回退默认目录()
    {
        var state = CreatePointer().Read();

        Assert.Equal(DefaultDirectory, state.DataDirectory);
        Assert.Null(state.PendingSource);
    }

    [Fact]
    public void Read_指针文件损坏时回退默认目录()
    {
        var pointer = CreatePointer();
        File.WriteAllText(pointer.PointerPath, "{ 这不是 JSON");

        Assert.Equal(DefaultDirectory, pointer.Read().DataDirectory);
    }

    [Fact]
    public void Read_待迁移源与生效目录相同视为无效()
    {
        var pointer = CreatePointer();
        pointer.Write(new DataDirectoryState { DataDirectory = DefaultDirectory, PendingSource = DefaultDirectory });

        Assert.Null(pointer.Read().PendingSource);
    }

    [Fact]
    public void ScheduleMigration_只写指针不动数据()
    {
        var service = CreateService();
        var marker = Path.Combine(DefaultDirectory, "mail.db");
        File.WriteAllText(marker, "db");

        service.ScheduleMigration(TargetDirectory);

        var status = service.GetStatus();
        Assert.True(status.HasPending);
        Assert.Equal(DefaultDirectory, status.CurrentDirectory);
        Assert.Equal(TargetDirectory, status.PendingTarget);
        Assert.True(File.Exists(marker));
        Assert.False(Directory.Exists(TargetDirectory));
    }

    [Fact]
    public void CancelPending_指针回到当前目录()
    {
        var service = CreateService();
        service.ScheduleMigration(TargetDirectory);

        service.CancelPending();

        var status = service.GetStatus();
        Assert.False(status.HasPending);
        Assert.Equal(DefaultDirectory, status.CurrentDirectory);
    }

    // ---------------- 预检 ----------------

    [Fact]
    public async Task Validate_网络路径被拒绝()
    {
        var result = await CreateService().ValidateTargetAsync(@"\\server\share\ZungMail");

        Assert.False(result.Ok);
        Assert.Contains("网络", result.Message);
    }

    [Fact]
    public async Task Validate_目标位于当前目录内部被拒绝()
    {
        var inside = Path.Combine(DefaultDirectory, "sub");

        var result = await CreateService().ValidateTargetAsync(inside);

        Assert.False(result.Ok);
        Assert.Contains("内部", result.Message);
    }

    [Fact]
    public async Task Validate_非空且不是数据目录被拒绝()
    {
        Directory.CreateDirectory(TargetDirectory);
        File.WriteAllText(Path.Combine(TargetDirectory, "照片.jpg"), "x");

        var result = await CreateService().ValidateTargetAsync(TargetDirectory);

        Assert.False(result.Ok);
        Assert.Contains("不是空的", result.Message);
    }

    [Fact]
    public async Task Validate_空目录通过()
    {
        Directory.CreateDirectory(TargetDirectory);

        var result = await CreateService().ValidateTargetAsync(TargetDirectory);

        Assert.True(result.Ok);
        Assert.False(result.WillBackupExisting);
    }

    [Fact]
    public async Task Validate_已有数据目录时提示需要备份()
    {
        File.WriteAllText(Path.Combine(DefaultDirectory, "mail.db"), "db");
        Directory.CreateDirectory(TargetDirectory);
        File.WriteAllText(Path.Combine(TargetDirectory, "mail.db"), "old");

        var result = await CreateService().ValidateTargetAsync(TargetDirectory);

        Assert.True(result.Ok);
        Assert.True(result.WillBackupExisting);
    }

    // ---------------- 迁移 ----------------

    [Fact]
    public async Task Migrate_复制数据并把库内绝对路径改到新目录()
    {
        var emlDir = Path.Combine(DefaultDirectory, "eml", "1", "2");
        Directory.CreateDirectory(emlDir);
        var attachment = Path.Combine(emlDir, "报价.pdf");
        await File.WriteAllTextAsync(attachment, "pdf");
        await File.WriteAllTextAsync(Path.Combine(DefaultDirectory, "templates.json"), "[]");

        var inlineUri = new Uri(attachment).AbsoluteUri;
        CreateDatabase(DefaultDirectory, attachment, $"<p>正文</p><img src=\"{inlineUri}\"/>");

        var service = CreateService();
        service.ScheduleMigration(TargetDirectory);

        var result = await service.MigratePendingAsync();

        Assert.True(result.Success, result.Message);

        // 文件都复制过来了
        var copiedAttachment = Path.Combine(TargetDirectory, "eml", "1", "2", "报价.pdf");
        Assert.True(File.Exists(copiedAttachment));
        Assert.True(File.Exists(Path.Combine(TargetDirectory, "mail.db")));
        Assert.True(File.Exists(Path.Combine(TargetDirectory, "templates.json")));

        // 附件路径与正文里的 file:// 前缀都指到新目录
        var (localPath, bodyHtml) = ReadPaths(Path.Combine(TargetDirectory, "mail.db"));
        Assert.StartsWith(Path.Combine(TargetDirectory, "eml"), localPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(new Uri(copiedAttachment).AbsoluteUri, bodyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(inlineUri, bodyHtml, StringComparison.Ordinal);

        // 指针切到新目录，旧目录原样保留
        var status = service.GetStatus();
        Assert.False(status.HasPending);
        Assert.Equal(TargetDirectory, status.CurrentDirectory);
        Assert.True(File.Exists(attachment));
        Assert.True(File.Exists(Path.Combine(DefaultDirectory, "mail.db")));
    }

    [Fact]
    public async Task Migrate_目标已有数据时先改名备份()
    {
        // 源目录是真实的 SQLite 库；目标目录里放一份「旧数据」占位文件（不会被当成库打开）
        CreateDatabase(DefaultDirectory, null, null);

        Directory.CreateDirectory(TargetDirectory);
        await File.WriteAllTextAsync(Path.Combine(TargetDirectory, "mail.db"), "old-db");

        var service = CreateService();
        service.ScheduleMigration(TargetDirectory);
        var result = await service.MigratePendingAsync();

        Assert.True(result.Success, result.Message);

        var backups = Directory.GetDirectories(_root, "moved_backup_*");
        Assert.Single(backups);
        Assert.Equal("old-db", await File.ReadAllTextAsync(Path.Combine(backups[0], "mail.db")));
    }

    [Fact]
    public async Task Migrate_没有待办时直接返回成功()
    {
        var result = await CreateService().MigratePendingAsync();

        Assert.True(result.Success);
    }

    // ---------------- 辅助 ----------------

    private static void CreateDatabase(string directory, string? attachmentPath, string? bodyHtml)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "mail.db"),
                Pooling = false
            }.ToString());
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
CREATE TABLE folders (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT);
CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, body_html TEXT, eml_path TEXT);
CREATE TABLE attachments (id INTEGER PRIMARY KEY AUTOINCREMENT, local_path TEXT);
""";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO folders (name) VALUES ('收件箱');";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO messages (body_html, eml_path) VALUES (@html, @eml);";
            command.Parameters.AddWithValue("@html", (object?)bodyHtml ?? DBNull.Value);
            command.Parameters.AddWithValue("@eml", (object?)attachmentPath ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        if (attachmentPath is null) return;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO attachments (local_path) VALUES (@path);";
            command.Parameters.AddWithValue("@path", attachmentPath);
            command.ExecuteNonQuery();
        }
    }

    private static (string LocalPath, string BodyHtml) ReadPaths(string databasePath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();

        string localPath = "";
        string body = "";

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT local_path FROM attachments LIMIT 1;";
            localPath = command.ExecuteScalar() as string ?? "";
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT body_html FROM messages LIMIT 1;";
            body = command.ExecuteScalar() as string ?? "";
        }

        return (localPath, body);
    }
}
