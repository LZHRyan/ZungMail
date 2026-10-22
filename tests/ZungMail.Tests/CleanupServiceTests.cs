using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Configuration;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>历史清理：筛选保护、执行、撤销、中断恢复与彻底删除。</summary>
[Trait("Category", "Integration")]
public sealed class CleanupServiceTests : IDisposable
{
    private const long InboxFolder = 1;
    private const long TrashFolder = 2;
    private const long JunkFolder = 3;

    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;
    private readonly CleanupService _cleanup;
    private int _nextUid = 100;

    public CleanupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();
        _messages = new SqliteMessageRepository(_factory);
        _cleanup = new CleanupService(
            _factory,
            _messages,
            paths,
            NullLogger<CleanupService>.Instance);
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

    private long AddMessage(
        long folderId,
        string subject,
        DateTimeOffset? date = null,
        long size = 1024,
        MessageFlags flags = MessageFlags.Seen,
        params (string Name, long Size)[] attachments)
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO messages(account_id, folder_id, uid, message_id, thread_id, subject, from_name, from_email,
                     to_json, cc_json, bcc_json, references_json, date_utc, preview, flags, size,
                     has_attachment, body_state, body_text)
VALUES(1, @folderId, @uid, @messageId, @threadId, @subject, '发件人', 'sender@example.com',
       '[]', '[]', '[]', '[]', @date, '', @flags, @size, @hasAttachment, 1, '正文');
SELECT last_insert_rowid();
""";
        var uid = Interlocked.Increment(ref _nextUid);
        command.Parameters.AddWithValue("@folderId", folderId);
        command.Parameters.AddWithValue("@uid", uid);
        command.Parameters.AddWithValue("@messageId", $"<{uid}@example.com>");
        command.Parameters.AddWithValue("@threadId", $"local:{uid}");
        command.Parameters.AddWithValue("@subject", subject);
        command.Parameters.AddWithValue("@date", (date ?? DateTimeOffset.UtcNow).ToUniversalTime()
            .ToString("o"));
        command.Parameters.AddWithValue("@flags", (int)flags);
        command.Parameters.AddWithValue("@size", size);
        command.Parameters.AddWithValue("@hasAttachment", attachments.Length > 0 ? 1 : 0);

        var id = Convert.ToInt64(command.ExecuteScalar());

        foreach (var (name, itemSize) in attachments)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
INSERT INTO attachments(message_id, file_name, content_type, size, is_inline, local_path)
VALUES(@messageId, @name, 'application/octet-stream', @size, 0, @path);
""";
            insert.Parameters.AddWithValue("@messageId", id);
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@size", itemSize);
            insert.Parameters.AddWithValue("@path", $@"C:\tmp\{name}");
            insert.ExecuteNonQuery();
        }

        return id;
    }

    private long CountMessages()
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task 预览_默认只覆盖垃圾箱与广告且保护星标未读草稿()
    {
        AddMessage(TrashFolder, "普通垃圾邮件");
        AddMessage(TrashFolder, "星标垃圾邮件", flags: MessageFlags.Seen | MessageFlags.Flagged);
        AddMessage(TrashFolder, "未读垃圾邮件", flags: (MessageFlags)0);
        AddMessage(TrashFolder, "垃圾箱里的草稿", flags: MessageFlags.Seen | MessageFlags.Draft);
        AddMessage(JunkFolder, "广告邮件");
        AddMessage(InboxFolder, "收件箱正常邮件");

        var preview = await _cleanup.PreviewAsync(new CleanupFilter());

        Assert.Equal(2, preview.TotalCount);
        Assert.Contains(preview.Items, i => i.Subject == "普通垃圾邮件");
        Assert.Contains(preview.Items, i => i.Subject == "广告邮件");
    }

    [Fact]
    public async Task 预览_支持时间与大小筛选()
    {
        AddMessage(TrashFolder, "很久以前", DateTimeOffset.UtcNow.AddDays(-400), 8 * 1024 * 1024);
        AddMessage(TrashFolder, "最近", DateTimeOffset.UtcNow.AddDays(-1), 1024);

        var preview = await _cleanup.PreviewAsync(new CleanupFilter
        {
            BeforeUtc = DateTimeOffset.UtcNow.AddDays(-30),
            MinSizeBytes = 1024 * 1024
        });

        Assert.Equal(1, preview.TotalCount);
        Assert.Equal("很久以前", Assert.Single(preview.Items).Subject);
    }

    [Fact]
    public async Task 执行_删除后可按审计快照完整撤销()
    {
        var attachmentId = AddMessage(
            TrashFolder,
            "待清理邮件",
            DateTimeOffset.UtcNow.AddDays(-60),
            2048,
            attachments: new[] { ("报表.pdf", 4096L) });

        var job = await _cleanup.ExecuteAsync(new CleanupFilter(), null, CleanupMode.Filter);

        Assert.Equal(CleanupStatus.Completed, job.Status);
        Assert.Equal(1, job.ProcessedCount);
        Assert.Null(await _messages.GetDetailAsync(attachmentId));
        Assert.Equal(0, CountMessages());

        var restored = await _cleanup.UndoAsync(job.Id);

        Assert.Equal(1, restored);
        Assert.Equal(1, CountMessages());
        var detail = await _messages.GetDetailAsync(attachmentId);
        Assert.NotNull(detail);
        Assert.Equal("待清理邮件", detail!.Subject);
        Assert.Equal("报表.pdf", Assert.Single(detail.Attachments).FileName);

        var jobs = await _cleanup.GetJobsAsync();
        Assert.Equal(CleanupStatus.Undone, Assert.Single(jobs).Status);
    }

    [Fact]
    public async Task 勾选清理_只处理选中的邮件()
    {
        var first = AddMessage(TrashFolder, "勾选一");
        var second = AddMessage(TrashFolder, "勾选二");
        AddMessage(TrashFolder, "未勾选");

        var job = await _cleanup.ExecuteAsync(
            new CleanupFilter(), new[] { first, second }, CleanupMode.Selected);

        Assert.Equal(CleanupStatus.Completed, job.Status);
        Assert.Equal(2, job.ProcessedCount);
        Assert.Equal(1, CountMessages());
        Assert.Null(await _messages.GetDetailAsync(first));
        Assert.Null(await _messages.GetDetailAsync(second));
    }

    [Fact]
    public async Task 彻底删除_清空隔离区并标记已释放()
    {
        var id = AddMessage(TrashFolder, "彻底删除");
        var quarantineItem = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(Path.Combine(quarantineItem, "1", id.ToString()));

        var job = await _cleanup.ExecuteAsync(new CleanupFilter(), null, CleanupMode.Filter);
        var freed = await _cleanup.PurgeAsync(job.Id);

        Assert.True(freed > 0);
        Assert.False(Directory.Exists(Path.Combine(quarantineItem, job.Id.ToString())));

        var audit = Assert.Single(await _cleanup.GetJobsAsync());
        Assert.Equal(CleanupStatus.Purged, audit.Status);
        Assert.True(audit.FreedBytes > 0);
    }

    [Fact]
    public async Task 中断后恢复_跳过已处理邮件并完成剩余()
    {
        const int total = 260;
        for (var i = 0; i < total; i++)
        {
            AddMessage(TrashFolder, "批量-" + i, DateTimeOffset.UtcNow.AddDays(-10));
        }

        using var cts = new CancellationTokenSource();
        var job = await _cleanup.ExecuteAsync(
            new CleanupFilter(), null, CleanupMode.Filter,
            new CancelAfterFirstBatch(200, cts), cts.Token);

        Assert.Equal(CleanupStatus.Cancelled, job.Status);
        Assert.Equal(200, job.ProcessedCount);
        Assert.Equal(60, CountMessages());

        var resumed = await _cleanup.ResumeAsync(job.Id);

        Assert.NotNull(resumed);
        Assert.Equal(CleanupStatus.Completed, resumed!.Status);
        Assert.Equal(total, resumed.ProcessedCount);
        Assert.Equal(0, CountMessages());
    }

    /// <summary>处理满阈值后立即取消，模拟用户中断（同步 Report 保证时序确定）。</summary>
    private sealed class CancelAfterFirstBatch : IProgress<CleanupProgress>
    {
        private readonly int _threshold;
        private readonly CancellationTokenSource _cts;

        public CancelAfterFirstBatch(int threshold, CancellationTokenSource cts)
        {
            _threshold = threshold;
            _cts = cts;
        }

        public void Report(CleanupProgress value)
        {
            if (value.Processed >= _threshold) _cts.Cancel();
        }
    }
}
