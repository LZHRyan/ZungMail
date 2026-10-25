using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>本地存储监控：分级阈值、开关与可释放空间估算。</summary>
[Trait("Category", "Integration")]
public sealed class StorageMonitorTests : IDisposable
{
    private const long InboxFolder = 1;
    private const long TrashFolder = 2;
    private const long JunkFolder = 3;

    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly StubSettingsStore _settings = new();
    private readonly StorageMonitor _monitor;
    private int _nextUid = 200;

    public StorageMonitorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-storage-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();
        _monitor = new StorageMonitor(
            paths,
            _settings,
            _factory,
            NullLogger<StorageMonitor>.Instance);
        SeedFolders();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响验证结论
        }
    }

    private void SeedFolders()
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO accounts(id, name, email, created_utc) VALUES (1, '测试账户', 'me@example.com', '2026-01-01T00:00:00.0000000+00:00');
INSERT INTO folders(id, account_id, full_name, display_name, kind) VALUES
    (1, 1, 'INBOX', '收件箱', 1),
    (2, 1, 'Trash', '垃圾箱', 4),
    (3, 1, 'Junk', '广告邮件', 5);
""";
        command.ExecuteNonQuery();
    }

    private void AddMessage(long folderId, long size, long attachmentBytes = 0)
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO messages(account_id, folder_id, uid, message_id, thread_id, subject, from_name, from_email,
                     to_json, cc_json, bcc_json, references_json, date_utc, preview, flags, size,
                     has_attachment, body_state, body_text)
VALUES(1, @folderId, @uid, @messageId, @threadId, '主题', '发件人', 'sender@example.com',
       '[]', '[]', '[]', '[]', @date, '', 1, @size, @hasAttachment, 1, '正文');
SELECT last_insert_rowid();
""";
        var uid = Interlocked.Increment(ref _nextUid);
        command.Parameters.AddWithValue("@folderId", folderId);
        command.Parameters.AddWithValue("@uid", uid);
        command.Parameters.AddWithValue("@messageId", $"<{uid}@example.com>");
        command.Parameters.AddWithValue("@threadId", $"local:{uid}");
        command.Parameters.AddWithValue("@date", DateTimeOffset.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@size", size);
        command.Parameters.AddWithValue("@hasAttachment", attachmentBytes > 0 ? 1 : 0);
        var id = Convert.ToInt64(command.ExecuteScalar());

        if (attachmentBytes <= 0) return;

        using var insert = connection.CreateCommand();
        insert.CommandText = """
INSERT INTO attachments(message_id, file_name, content_type, size, is_inline, local_path)
VALUES(@messageId, '附件.pdf', 'application/pdf', @size, 0, @path);
""";
        insert.Parameters.AddWithValue("@messageId", id);
        insert.Parameters.AddWithValue("@size", attachmentBytes);
        insert.Parameters.AddWithValue("@path", $@"C:\tmp\{id}.pdf");
        insert.ExecuteNonQuery();
    }

    /// <summary>把设置的上限调整到让占用比例落在目标百分比附近。</summary>
    private async Task<StorageAlertLevel> SetUsageAsync(double percent)
    {
        var probe = await _monitor.GetSnapshotAsync();
        var total = probe.DatabaseBytes + probe.AttachmentBytes;
        _settings.Current.StorageLimitBytes = Math.Max(1, (long)(total * 100.0 / percent));
        return (await _monitor.GetSnapshotAsync()).Level;
    }

    [Fact]
    public void 分级阈值_按默认75_85_95判定()
    {
        // 纯策略判定：不读真实磁盘，避免 SQLite 页分配让占用率在两次快照之间漂移
        Assert.Equal(StorageAlertLevel.Normal, Level(60));
        Assert.Equal(StorageAlertLevel.Normal, Level(74.9));
        Assert.Equal(StorageAlertLevel.Notice, Level(75));
        Assert.Equal(StorageAlertLevel.Notice, Level(80));
        Assert.Equal(StorageAlertLevel.Warning, Level(90));
        Assert.Equal(StorageAlertLevel.Critical, Level(97));
    }

    [Fact]
    public void 分级阈值_支持自定义百分比()
    {
        _settings.Current.StorageNoticePercent = 50;
        _settings.Current.StorageWarningPercent = 60;
        _settings.Current.StorageCriticalPercent = 70;

        Assert.Equal(StorageAlertLevel.Notice, Level(55));
        Assert.Equal(StorageAlertLevel.Warning, Level(65));
        Assert.Equal(StorageAlertLevel.Critical, Level(75));
    }

    [Fact]
    public async Task 快照的占用率与级别一致()
    {
        AddMessage(TrashFolder, 1024);
        await SetUsageAsync(60);

        var snapshot = await _monitor.GetSnapshotAsync();

        Assert.True(snapshot.UsagePercent > 0);
        // 集成层只验接线：快照里的级别必须等于同一占用率过策略函数的结果，不卡边界
        Assert.Equal(StorageMonitor.Evaluate(_settings.Current, snapshot.UsagePercent, out _), snapshot.Level);
    }

    private StorageAlertLevel Level(double usage)
        => StorageMonitor.Evaluate(_settings.Current, usage, out _);
    [Fact]
    public async Task 关闭预警开关后始终为正常()
    {
        AddMessage(TrashFolder, 1024);
        _settings.Current.StorageAlertsEnabled = false;

        var probe = await _monitor.GetSnapshotAsync();
        var total = probe.DatabaseBytes + probe.AttachmentBytes;
        _settings.Current.StorageLimitBytes = Math.Max(1, (long)(total * 100.0 / 99));

        var snapshot = await _monitor.GetSnapshotAsync();

        Assert.Equal(StorageAlertLevel.Normal, snapshot.Level);
        Assert.Equal("", snapshot.AlertText);
    }

    [Fact]
    public async Task 可释放空间_只统计垃圾箱与广告邮件及其附件()
    {
        AddMessage(TrashFolder, 1000, 4000);
        AddMessage(JunkFolder, 2000, 8000);
        AddMessage(InboxFolder, 5000, 99999);

        var snapshot = await _monitor.GetSnapshotAsync();

        Assert.Equal(3, snapshot.MessageCount);
        Assert.Equal(3, snapshot.AttachmentCount);
        Assert.Equal(15000, snapshot.ReclaimableBytes);
        Assert.Equal(111999, snapshot.AttachmentBytes);
    }

    private sealed class StubSettingsStore : ISettingsStore
    {
        public AppSettings Current { get; } = new();

        public string? LoadWarning => null;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}