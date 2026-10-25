using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 退信的存储口径：四列随迁移新增、标识与细节持久化、重新 upsert / 补正文都不会洗掉，
/// UpdateBounceAsync 只填细节不清空已有值。
/// </summary>
[Trait("Category", "Integration")]
public sealed class BounceRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;

    public BounceRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-bounce-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();
        _messages = new SqliteMessageRepository(_factory);
        SeedAccountAndFolder();
    }

    private void SeedAccountAndFolder()
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO accounts(id, name, email, created_utc) VALUES (1, '测试账户', 'me@example.com', '2026-01-01T00:00:00.0000000+00:00');
INSERT INTO folders(id, account_id, full_name, display_name, kind) VALUES (1, 1, 'INBOX', '收件箱', 1);
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

    private Task<long> AddMessageAsync(uint uid, bool isBounce, string recipient = "", string status = "", string detail = "")
        => _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = uid,
            Subject = "Undelivered Mail Returned to Sender " + uid,
            FromEmail = "mailer-daemon@example.com",
            DateUtc = DateTimeOffset.UtcNow,
            IsBounce = isBounce,
            BounceRecipient = recipient,
            BounceStatus = status,
            BounceDetail = detail
        });

    [Fact]
    public void 迁移新增四个退信列且存量邮件默认非退信()
    {
        using var connection = _factory.Create();

        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = """
SELECT COUNT(*) FROM pragma_table_info('messages')
WHERE name IN ('is_bounce', 'bounce_recipient', 'bounce_status', 'bounce_detail');
""";
            Assert.Equal(4L, Convert.ToInt64(columns.ExecuteScalar()));
        }

        // 迁移只加列不判定：按老口径插入的行（不写退信列）必须落到默认值
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
INSERT INTO messages(account_id, folder_id, uid, from_email, date_utc)
VALUES(1, 1, 42, 'old@example.com', '2026-01-01T00:00:00.0000000+00:00');
""";
            insert.ExecuteNonQuery();
        }

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT is_bounce, bounce_recipient FROM messages WHERE uid = 42;";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal("", reader.GetString(1));
    }

    [Fact]
    public async Task 退信标识与细节随邮件持久化并回读()
    {
        await AddMessageAsync(3, isBounce: true, recipient: "ops@forwarder.cn", status: "5.1.1", detail: "550 User unknown");

        var detail = await _messages.FindByUidAsync(1, 3);

        Assert.NotNull(detail);
        Assert.True(detail!.IsBounce);
        Assert.True(detail.HasBounceInfo);
        Assert.Equal("ops@forwarder.cn", detail.BounceRecipient);
        Assert.Equal("5.1.1", detail.BounceStatus);
        Assert.Equal("550 User unknown", detail.BounceDetail);
        Assert.Contains("收件人 ops@forwarder.cn", detail.BounceDetailLine);
    }

    [Fact]
    public async Task 列表查询也带出退信字段()
    {
        await AddMessageAsync(4, isBounce: true, recipient: "ops@forwarder.cn", status: "5.2.2");

        var page = await _messages.QueryAsync(new MessageQuery { FolderId = 1 });

        var row = Assert.Single(page.Items);
        Assert.True(row.IsBounce);
        Assert.Equal("ops@forwarder.cn", row.BounceRecipient);
        Assert.Equal("5.2.2", row.BounceStatus);
    }

    [Fact]
    public async Task 补正文再次写入不会洗掉退信标识与细节()
    {
        await AddMessageAsync(5, isBounce: true, recipient: "ops@forwarder.cn", status: "5.1.1", detail: "550 User unknown");
        var detail = await _messages.FindByUidAsync(1, 5);
        Assert.NotNull(detail);

        // 正文下载完成后再次 upsert（此时调用方不带退信字段），标识与细节必须保留
        await AddMessageAsync(5, isBounce: false);
        await _messages.SaveBodyAsync(detail!.Id, "<p>退信正文</p>", "退信正文", Array.Empty<AttachmentInfo>());

        var reread = await _messages.FindByUidAsync(1, 5);
        Assert.NotNull(reread);
        Assert.True(reread!.IsBounce);
        Assert.Equal("ops@forwarder.cn", reread.BounceRecipient);
        Assert.Equal("5.1.1", reread.BounceStatus);
    }

    [Fact]
    public async Task UpdateBounceAsync回填细节且空值不清空已有内容()
    {
        var id = await AddMessageAsync(6, isBounce: true);

        await _messages.UpdateBounceAsync(id, true, "ops@forwarder.cn", "5.1.1", "550 User unknown");
        var first = await _messages.GetDetailAsync(id);
        Assert.Equal("ops@forwarder.cn", first!.BounceRecipient);

        // 再次下载正文没解析出细节：只保留原值，不清空
        await _messages.UpdateBounceAsync(id, true, "", "", "");
        var second = await _messages.GetDetailAsync(id);
        Assert.True(second!.IsBounce);
        Assert.Equal("ops@forwarder.cn", second.BounceRecipient);
        Assert.Equal("5.1.1", second.BounceStatus);
        Assert.Equal("550 User unknown", second.BounceDetail);
    }

    [Fact]
    public async Task 非退信邮件UpdateBounceAsync不会把它变成退信()
    {
        var id = await AddMessageAsync(7, isBounce: false);

        await _messages.UpdateBounceAsync(id, false, "", "", "");

        var detail = await _messages.GetDetailAsync(id);
        Assert.False(detail!.IsBounce);
    }
}
