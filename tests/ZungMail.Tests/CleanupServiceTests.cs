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

    [Fact]
    public async Task 预览_支持分页且页间不重叠()
    {
        for (var i = 0; i < 5; i++)
        {
            AddMessage(TrashFolder, "分页-" + i, DateTimeOffset.UtcNow.AddDays(-i - 1));
        }

        var page1 = await _cleanup.PreviewAsync(new CleanupFilter(), take: 2, skip: 0);
        var page2 = await _cleanup.PreviewAsync(new CleanupFilter(), take: 2, skip: 2);
        var page3 = await _cleanup.PreviewAsync(new CleanupFilter(), take: 2, skip: 4);

        // 总量始终是匹配总数，不随分页变化
        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(2, page2.Items.Count);
        Assert.Single(page3.Items);

        // 按时间倒序，页与页之间不重复
        Assert.Equal("分页-0", page1.Items[0].Subject);
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page2.Items.Select(i => i.Id)));
        Assert.Empty(page2.Items.Select(i => i.Id).Intersect(page3.Items.Select(i => i.Id)));
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page3.Items.Select(i => i.Id)));
    }

    [Fact]
    public async Task 全选ID_按筛选返回全部匹配且仍受保护条件限制()
    {
        var trash = AddMessage(TrashFolder, "垃圾箱普通邮件");
        AddMessage(InboxFolder, "收件箱邮件");
        AddMessage(TrashFolder, "星标邮件", flags: MessageFlags.Seen | MessageFlags.Flagged);

        var ids = await _cleanup.GetMatchingIdsAsync(new CleanupFilter());

        Assert.Equal(new[] { trash }, ids);
    }

    [Fact]
    public async Task 仅清正文_保留邮件头并清空正文与附件文件()
    {
        var id = AddMessage(TrashFolder, "老邮件", attachments: new[] { ("报表.pdf", 512L) });
        var directory = Path.Combine(_root, "eml", "1", id.ToString());
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "报表.pdf"), new byte[512]);

        var filter = new CleanupFilter { ClearBodyOnly = true };

        // 预览的可释放口径＝正文 + 附件，不是整封邮件大小
        var preview = await _cleanup.PreviewAsync(filter);
        Assert.Equal(1, preview.TotalCount);
        Assert.True(preview.ReclaimableBytes > 0);
        Assert.True(preview.ReclaimableBytes < preview.TotalBytes + preview.AttachmentBytes);

        var job = await _cleanup.ExecuteAsync(filter, null, CleanupMode.Filter);

        Assert.True(job.IsBodyOnly);
        Assert.Equal(CleanupStatus.Completed, job.Status);
        Assert.Equal(1, job.ProcessedCount);
        Assert.True(job.TotalBytes > 0);
        Assert.False(job.CanUndo);
        Assert.False(job.CanPurge);

        // 邮件本身留着：主题等邮件头不变，正文与附件都清空
        Assert.Equal(1, CountMessages());
        var detail = await _messages.GetDetailAsync(id);
        Assert.NotNull(detail);
        Assert.Equal("老邮件", detail!.Subject);
        Assert.Empty(detail.Attachments);
        Assert.Equal((0, (string?)null), ReadBody(id));
        Assert.Equal(0, CountAttachments(id));
        Assert.False(Directory.Exists(directory));

        // 审计记录能识别成「仅清正文」，且不会被撤销 / 彻底删除改写状态
        var audit = Assert.Single(await _cleanup.GetJobsAsync());
        Assert.True(audit.IsBodyOnly);
        Assert.Contains("仅清正文", audit.ModeDisplay);
        Assert.Equal(0, await _cleanup.UndoAsync(job.Id));
        Assert.Equal(0, await _cleanup.PurgeAsync(job.Id));
        Assert.Equal(CleanupStatus.Completed, Assert.Single(await _cleanup.GetJobsAsync()).Status);
    }

    [Fact]
    public async Task 仅清正文_仍受星标与未读保护限制()
    {
        var starred = AddMessage(TrashFolder, "星标老邮件", flags: MessageFlags.Seen | MessageFlags.Flagged);
        var normal = AddMessage(TrashFolder, "普通老邮件");

        var job = await _cleanup.ExecuteAsync(
            new CleanupFilter { ClearBodyOnly = true }, null, CleanupMode.Filter);

        Assert.Equal(1, job.ProcessedCount);
        Assert.Equal(2, CountMessages());
        Assert.Equal((1, (string?)"正文"), ReadBody(starred));
        Assert.Equal((0, (string?)null), ReadBody(normal));
    }

    private (int BodyState, string? BodyText) ReadBody(long messageId)
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body_state, body_text FROM messages WHERE id = @id;";
        command.Parameters.AddWithValue("@id", messageId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : (-1, null);
    }

    private long CountAttachments(long messageId)
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM attachments WHERE message_id = @id;";
        command.Parameters.AddWithValue("@id", messageId);
        return Convert.ToInt64(command.ExecuteScalar());
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
