using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>邮件仓储读写：正文与附件的持久化与回读。</summary>
[Trait("Category", "Integration")]
public sealed class MessageRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;

    public MessageRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-msg-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();
        _messages = new SqliteMessageRepository(_factory);
        SeedAccountAndFolder();
    }

    /// <summary>messages.folder_id 有外键约束，先落一个账户与收件箱文件夹。</summary>
    private void SeedAccountAndFolder()
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO accounts(id, name, email, created_utc) VALUES (1, '测试账户', 'me@example.com', '2026-01-01T00:00:00.0000000+00:00');
INSERT INTO folders(id, account_id, full_name, display_name, kind) VALUES (1, 1, 'INBOX', '收件箱', 0);
""";
        command.ExecuteNonQuery();
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

    private Task<long> AddAsync() => _messages.UpsertAsync(new EmailDetail
    {
        AccountId = 1,
        FolderId = 1,
        Uid = 10,
        Subject = "附件验证",
        FromEmail = "sender@example.com",
        DateUtc = DateTimeOffset.UtcNow
    });

    [Fact]
    public async Task 详情_回读附件列表并保留内嵌标记()
    {
        var id = await AddAsync();

        await _messages.SaveBodyAsync(id, "<p>正文</p>", "正文", new[]
        {
            new AttachmentInfo
            {
                FileName = "报价单.pdf",
                ContentType = "application/pdf",
                Size = 2048,
                LocalPath = @"C:\tmp\报价单.pdf"
            },
            new AttachmentInfo
            {
                FileName = "logo.png",
                ContentType = "image/png",
                Size = 512,
                IsInline = true,
                LocalPath = @"C:\tmp\logo.png"
            }
        });

        var detail = await _messages.GetDetailAsync(id);

        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Attachments.Count);

        var attachment = Assert.Single(detail.Attachments, a => !a.IsInline);
        Assert.Equal("报价单.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(2048, attachment.Size);
        Assert.Equal(@"C:\tmp\报价单.pdf", attachment.LocalPath);

        var inline = Assert.Single(detail.Attachments, a => a.IsInline);
        Assert.Equal("logo.png", inline.FileName);
    }

    [Fact]
    public async Task 详情_未下载正文时无附件()
    {
        var id = await AddAsync();

        var detail = await _messages.GetDetailAsync(id);

        Assert.NotNull(detail);
        Assert.Empty(detail!.Attachments);
    }

    [Fact]
    public async Task 搜索_命中附件名称()
    {
        var id = await AddAsync();
        await _messages.SaveBodyAsync(id, "<p>正文</p>", "正文", new[]
        {
            new AttachmentInfo
            {
                FileName = "2026年季度报表.xlsx",
                ContentType = "application/vnd.ms-excel",
                Size = 4096,
                LocalPath = @"C:\tmp\2026年季度报表.xlsx"
            }
        });

        var result = await _messages.QueryAsync(new MessageQuery { SearchText = "季度报表", Take = 20 });

        Assert.Contains(result.Items, item => item.Id == id);
    }

    [Fact]
    public async Task 搜索_命中收件人与抄送()
    {
        var id = await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 20,
            Subject = "会议纪要",
            FromEmail = "sender@example.com",
            To = { new EmailAddress("wang@example.com", "王小明") },
            Cc = { new EmailAddress("li@example.com", "李华") },
            DateUtc = DateTimeOffset.UtcNow
        });

        var byTo = await _messages.QueryAsync(new MessageQuery { SearchText = "王小明", Take = 20 });
        var byCc = await _messages.QueryAsync(new MessageQuery { SearchText = "李华", Take = 20 });

        Assert.Contains(byTo.Items, item => item.Id == id);
        Assert.Contains(byCc.Items, item => item.Id == id);
    }

    [Fact]
    public async Task 重建索引_可补齐附件名索引()
    {
        var id = await AddAsync();
        await _messages.SaveBodyAsync(id, "<p>正文</p>", "正文", new[]
        {
            new AttachmentInfo { FileName = "合同扫描件.pdf", Size = 1024 }
        });

        // 模拟旧版本遗留：附件名尚未进入索引
        using (var connection = _factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE messages SET fts_body = '' WHERE id = @id;";
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }

        Assert.DoesNotContain("合", ReadFtsBody(id));

        var processed = await _messages.RebuildSearchIndexAsync();

        Assert.True(processed >= 1);
        Assert.Contains("合", ReadFtsBody(id));

        var after = await _messages.QueryAsync(new MessageQuery { SearchText = "合同扫描件", Take = 20 });
        Assert.Contains(after.Items, item => item.Id == id);
    }

    private string ReadFtsBody(long id)
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(fts_body, '') FROM messages WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    [Fact]
    public async Task 待预取正文_按窗口与上限筛选并跳过已下载()
    {
        var old = await AddDatedAsync(uid: 1, daysAgo: 30);
        var recent = await AddDatedAsync(uid: 2, daysAgo: 2);
        var newest = await AddDatedAsync(uid: 3, daysAgo: 0.5);
        await _messages.SaveBodyAsync(newest, "<p>已有正文</p>", "已有正文", Array.Empty<AttachmentInfo>());

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var pending = await _messages.GetPendingBodyDownloadsAsync(1, since, 10);

        var single = Assert.Single(pending);
        Assert.Equal(recent, single.Id);
        Assert.Equal(2u, single.Uid);

        // 不限时间时窗口外的历史邮件也进入清单，但受上限约束（取最近的一封，已下载的跳过）
        var limited = await _messages.GetPendingBodyDownloadsAsync(1, null, 1);
        Assert.Equal(recent, Assert.Single(limited).Id);

        var all = await _messages.GetPendingBodyDownloadsAsync(1, null, 10);
        Assert.Equal(new[] { recent, old }, all.Select(item => item.Id).ToArray());
    }

    /// <summary>按指定时间落一封未下载正文的邮件，用于验证预取窗口。</summary>
    private Task<long> AddDatedAsync(uint uid, double daysAgo) => _messages.UpsertAsync(new EmailDetail
    {
        AccountId = 1,
        FolderId = 1,
        Uid = uid,
        Subject = $"邮件 {uid}",
        FromEmail = "sender@example.com",
        DateUtc = DateTimeOffset.UtcNow.AddDays(-daysAgo)
    });

    [Fact]
    public async Task 存储占用_按整封大小求和_正文长度只给零大小行兜底()
    {
        // 有 size：直接用它，不再读正文（旧写法把正文重复加了一遍，还要扫全表正文）
        await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 401,
            Subject = "有大小",
            FromEmail = "a@example.com",
            DateUtc = DateTimeOffset.UtcNow,
            Size = 1000,
            BodyState = BodyState.Downloaded,
            BodyText = "0123456789"
        });

        // 没有 size（本地导入 / 服务器没给）：回退用正文字符数估算
        await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 402,
            Subject = "无大小",
            FromEmail = "b@example.com",
            DateUtc = DateTimeOffset.UtcNow,
            Size = 0,
            BodyState = BodyState.Downloaded,
            BodyText = "0123456789"
        });

        var bytes = await _messages.GetStorageSizeAsync();

        Assert.Equal(1000 + 10, bytes);
    }

    [Fact]
    public async Task 文件夹计数_排除本地已删除且与列表口径一致()
    {
        // 收件箱：1 封未读 + 1 封已读；另一封未读但已标 \Deleted（本地已删、服务器未 EXPUNGE）
        await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1, FolderId = 1, Uid = 501, Subject = "未读",
            FromEmail = "a@example.com", DateUtc = DateTimeOffset.UtcNow
        });
        await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1, FolderId = 1, Uid = 502, Subject = "已读",
            FromEmail = "a@example.com", DateUtc = DateTimeOffset.UtcNow,
            Flags = MessageFlags.Seen
        });
        await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1, FolderId = 1, Uid = 503, Subject = "已删除",
            FromEmail = "a@example.com", DateUtc = DateTimeOffset.UtcNow,
            Flags = MessageFlags.Deleted
        });

        var counts = await _messages.GetFolderCountsAsync();
        Assert.Equal((2, 1), counts[1]);

        // 整目录本地一条都不剩（全被移走 / 删除）：不返回条目，界面据此把角标归零
        using (var connection = _factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO folders(id, account_id, full_name, display_name, kind) VALUES (3, 1, 'Junk', '垃圾邮件', 5);";
            command.ExecuteNonQuery();
        }
        await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1, FolderId = 3, Uid = 900, Subject = "垃圾",
            FromEmail = "spam@example.com", DateUtc = DateTimeOffset.UtcNow,
            Flags = MessageFlags.Deleted
        });

        var after = await _messages.GetFolderCountsAsync();
        Assert.False(after.ContainsKey(3));
        Assert.Equal((2, 1), after[1]);
    }
    [Fact]
    public async Task 全部已读_跨分页把未读邮件全部置为已读()
    {
        // uid 1..3 未读，uid 4 已读
        for (uint uid = 1; uid <= 4; uid++)
        {
            await _messages.UpsertAsync(new EmailDetail
            {
                AccountId = 1,
                FolderId = 1,
                Uid = uid,
                Subject = $"邮件 {uid}",
                FromEmail = "sender@example.com",
                DateUtc = DateTimeOffset.UtcNow.AddMinutes(-uid),
                Flags = uid == 4 ? MessageFlags.Seen : MessageFlags.None
            });
        }

        // Take 只取 1 封，验证不是仅处理当前页
        var targets = await _messages.MarkAllSeenAsync(new MessageQuery { FolderId = 1, Take = 1 });

        Assert.Equal(3, targets.Count);
        Assert.All(targets, t => Assert.Equal(MessageFlags.Seen, t.Flags & MessageFlags.Seen));

        var page = await _messages.QueryAsync(new MessageQuery { FolderId = 1, Take = 50 });
        Assert.All(page.Items, item => Assert.False(item.IsUnread));
    }

    [Fact]
    public async Task 列表与计数_排除本地已删除邮件()
    {
        await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 30,
            Subject = "待删除",
            FromEmail = "sender@example.com",
            DateUtc = DateTimeOffset.UtcNow
        });
        await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 31,
            Subject = "已删除",
            FromEmail = "sender@example.com",
            DateUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Flags = MessageFlags.Deleted
        });

        var page = await _messages.QueryAsync(new MessageQuery { FolderId = 1, Take = 50 });

        Assert.Single(page.Items);
        Assert.Equal("待删除", page.Items[0].Subject);
        Assert.Equal(1, await _messages.CountAsync(folderId: 1));

        // 同步校准快照不包含已删除邮件，避免服务器旧状态把 \Deleted 覆盖回去
        var flags = await _messages.GetFlagsAsync(1, 0);
        Assert.Single(flags);
        Assert.Equal(30u, flags[0].Uid);
        // 快照要带主键：同步端靠它直接批量更新，不再逐封点查询
        Assert.True(flags[0].Id > 0);
    }

    [Fact]
    public async Task QueryThreadsAsync_按标签过滤_会话与标签可组合()
    {
        var first = await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 50,
            Subject = "订舱确认",
            FromEmail = "sender@example.com",
            DateUtc = DateTimeOffset.UtcNow,
            ThreadId = "thread-a"
        });
        await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 51,
            Subject = "订舱确认",
            FromEmail = "sender@example.com",
            DateUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            ThreadId = "thread-a"
        });

        using (var connection = _factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
INSERT INTO mail_tags(id, name) VALUES (9, '订舱');
INSERT INTO message_tags(email_id, tag_id, created_utc) VALUES (@email, 9, '2026-01-01T00:00:00.0000000+00:00');
""";
            command.Parameters.AddWithValue("@email", first);
            command.ExecuteNonQuery();
        }

        var groups = await _messages.QueryThreadsAsync(new MessageQuery { FolderId = 1, TagId = 9, Take = 20 });

        var group = Assert.Single(groups.Items);
        Assert.Equal(1, groups.TotalCount);
        Assert.True(group.Count >= 1);
    }
    [Fact]
    public async Task QueryAsync_按标签过滤_只返回已打标签邮件()
    {
        var taggedId = await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 40,
            Subject = "已打标签",
            FromEmail = "sender@example.com",
            DateUtc = DateTimeOffset.UtcNow
        });
        await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 41,
            Subject = "未打标签",
            FromEmail = "sender@example.com",
            DateUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        using (var connection = _factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
INSERT INTO mail_tags(id, name, color) VALUES (7, '订舱', '#2563eb');
INSERT INTO message_tags(email_id, tag_id, created_utc) VALUES (@email, 7, '2026-01-01T00:00:00.0000000+00:00');
""";
            command.Parameters.AddWithValue("@email", taggedId);
            command.ExecuteNonQuery();
        }

        var page = await _messages.QueryAsync(new MessageQuery { FolderId = 1, TagId = 7, Take = 50 });

        var item = Assert.Single(page.Items);
        Assert.Equal("已打标签", item.Subject);
        Assert.Equal(1, page.TotalCount);
    }
}
