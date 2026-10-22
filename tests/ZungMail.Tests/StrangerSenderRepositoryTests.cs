using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 陌生发件人的存储口径：已知地址判定（通讯录 + 历史来信）、标识持久化与「不被后续 upsert 洗掉」、
/// 迁移后存量邮件一律非陌生。
/// </summary>
[Trait("Category", "Integration")]
public sealed class StrangerSenderRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;
    private readonly SqliteContactRepository _contacts;

    public StrangerSenderRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-stranger-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();
        _messages = new SqliteMessageRepository(_factory);
        _contacts = new SqliteContactRepository(_factory);
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

    private Task<long> AddMessageAsync(string fromEmail, uint uid, bool isStranger = false)
        => _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = uid,
            Subject = "测试 " + uid,
            FromEmail = fromEmail,
            DateUtc = DateTimeOffset.UtcNow,
            IsStranger = isStranger
        });

    private bool ReadStrangerFlag(uint uid)
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_stranger FROM messages WHERE folder_id = 1 AND uid = @uid;";
        command.Parameters.AddWithValue("@uid", (long)uid);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L) == 1L;
    }

    [Fact]
    public async Task 已知地址_通讯录与历史来信都算熟人()
    {
        await _contacts.UpsertManyAsync(new[] { new Contact { Email = "imported@partner.com", Name = "导入联系人" } });
        await AddMessageAsync("history@forwarder.cn", 1);

        var known = await _messages.GetKnownSenderAddressesAsync(new[]
        {
            "imported@partner.com",
            "HISTORY@forwarder.cn",
            "brandnew@example.com"
        });

        Assert.Contains("imported@partner.com", known);
        Assert.Contains("history@forwarder.cn", known);
        Assert.DoesNotContain("brandnew@example.com", known);
    }

    [Fact]
    public async Task 已知地址_空集合与空地址不炸()
    {
        Assert.Empty(await _messages.GetKnownSenderAddressesAsync(Array.Empty<string>()));
        Assert.Empty(await _messages.GetKnownSenderAddressesAsync(new[] { "", "   " }));
    }

    [Fact]
    public async Task 已知地址_超过分批上限仍能全部命中()
    {
        // 查询按 200 个地址分批，跨批次的结果必须都回来
        var emails = Enumerable.Range(0, 450).Select(i => $"bulk{i}@example.com").ToList();
        await _contacts.UpsertManyAsync(emails.Take(30).Select(email => new Contact { Email = email }));
        await AddMessageAsync("bulk400@example.com", 7);

        var known = await _messages.GetKnownSenderAddressesAsync(emails);

        Assert.Contains("bulk0@example.com", known);
        Assert.Contains("bulk29@example.com", known);
        Assert.Contains("bulk400@example.com", known);
        Assert.DoesNotContain("bulk100@example.com", known);
    }

    [Fact]
    public async Task 陌生标识随邮件持久化并回读()
    {
        await AddMessageAsync("newagent@example.com", 3, isStranger: true);

        var detail = await _messages.FindByUidAsync(1, 3);

        Assert.NotNull(detail);
        Assert.True(detail!.IsStranger);
    }

    [Fact]
    public async Task 补正文再次写入不会洗掉陌生标识()
    {
        await AddMessageAsync("newagent@example.com", 5, isStranger: true);
        var detail = await _messages.FindByUidAsync(1, 5);
        Assert.NotNull(detail);

        // 正文下载完成后再 upsert 一次（此时调用方不再传 IsStranger），标识必须保留
        await AddMessageAsync("newagent@example.com", 5, isStranger: false);
        await _messages.SaveBodyAsync(detail!.Id, "<p>正文</p>", "正文", Array.Empty<AttachmentInfo>());

        Assert.True(ReadStrangerFlag(5));
    }

    [Fact]
    public void 迁移后存量邮件一律非陌生且索引存在()
    {
        // 迁移只加列不判定：直接按老口径插入的行（不写 is_stranger）必须落到默认值 0
        using (var connection = _factory.Create())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
INSERT INTO messages(account_id, folder_id, uid, from_email, date_utc)
VALUES(1, 1, 42, 'old@example.com', '2026-01-01T00:00:00.0000000+00:00');
""";
            insert.ExecuteNonQuery();
        }

        Assert.False(ReadStrangerFlag(42));

        using var verify = _factory.Create();
        using var index = verify.CreateCommand();
        index.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_messages_from_email';";
        Assert.Equal(1L, Convert.ToInt64(index.ExecuteScalar()));
    }
}
