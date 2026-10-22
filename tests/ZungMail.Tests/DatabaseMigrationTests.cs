using System.Text;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>数据库迁移：旧版本库按 schema_version 有序升级到最新结构，重复执行保持幂等。</summary>
[Trait("Category", "Integration")]
public sealed class DatabaseMigrationTests : IDisposable
{
    private readonly string _root;

    public DatabaseMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-migrate-" + Guid.NewGuid().ToString("N")[..8]);
    }

    [Fact]
    public void Initialize_版本1旧库_升级到最新版本并补齐新列()
    {
        var factory = CreateFactory();

        using (var connection = factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
INSERT INTO meta(key, value) VALUES('schema_version', '1');
CREATE TABLE outbox(
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id       INTEGER NOT NULL,
    created_utc      TEXT NOT NULL,
    next_attempt_utc TEXT
);
""";
            command.ExecuteNonQuery();
        }

        new DatabaseInitializer(factory).Initialize();

        using var verify = factory.Create();
        using (var version = verify.CreateCommand())
        {
            version.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            Assert.Equal(DatabaseInitializer.SchemaVersion.ToString(), Convert.ToString(version.ExecuteScalar()));
        }

        using var check = verify.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('outbox') WHERE name = 'message_id';";
        Assert.Equal(1L, Convert.ToInt64(check.ExecuteScalar()));

        using var pending = verify.CreateCommand();
        pending.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'pending_operations';";
        Assert.Equal(1L, Convert.ToInt64(pending.ExecuteScalar()));
    }

    [Fact]
    public void Initialize_版本2库_补齐待同步操作队列表()
    {
        var factory = CreateFactory();

        using (var connection = factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
INSERT INTO meta(key, value) VALUES('schema_version', '2');
CREATE TABLE outbox(
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id       INTEGER NOT NULL,
    created_utc      TEXT NOT NULL,
    next_attempt_utc TEXT,
    message_id       TEXT
);
""";
            command.ExecuteNonQuery();
        }

        new DatabaseInitializer(factory).Initialize();

        using var verify = factory.Create();
        using (var version = verify.CreateCommand())
        {
            version.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            Assert.Equal(DatabaseInitializer.SchemaVersion.ToString(), Convert.ToString(version.ExecuteScalar()));
        }

        using var table = verify.CreateCommand();
        table.CommandText = """
SELECT COUNT(*) FROM sqlite_master
WHERE type = 'table' AND name = 'pending_operations'
  AND sql LIKE '%last_error%';
""";
        Assert.Equal(1L, Convert.ToInt64(table.ExecuteScalar()));
    }

    [Fact]
    public async Task Initialize_升级后_规则表可用()
    {
        var factory = CreateFactory();
        new DatabaseInitializer(factory).Initialize();

        var repository = new SqliteMailRuleRepository(factory);
        var id = await repository.UpsertAsync(new MailRule
        {
            Name = "船司主题标星",
            SubjectContains = "Booking Confirmation",
            Action = MailRuleAction.Star,
            Enabled = true,
            ContinueAfterMatch = true
        });

        await repository.RecordHitAsync(id, DateTimeOffset.UtcNow);

        var rule = Assert.Single(await repository.GetAllAsync(onlyEnabled: true));
        Assert.Equal(MailRuleAction.Star, rule.Action);
        Assert.Equal(1, rule.HitCount);
        Assert.NotNull(rule.LastHitUtc);
        Assert.True(rule.ContinueAfterMatch);

        // 关闭「继续执行」后应能回到默认语义（移动类动作终止后续规则）。
        rule.ContinueAfterMatch = false;
        rule.Action = MailRuleAction.Archive;
        await repository.UpsertAsync(rule);

        var updated = await repository.GetAsync(id);
        Assert.NotNull(updated);
        Assert.False(updated!.ContinueAfterMatch);
        Assert.Equal(MailRuleAction.Archive, updated.Action);
    }

    [Fact]
    public async Task Initialize_升级后_发件人黑白名单可用且幂等()
    {
        var factory = CreateFactory();
        new DatabaseInitializer(factory).Initialize();

        var repository = new SqliteSenderFilterRepository(factory);

        var blackId = await repository.UpsertAsync(new SenderFilter
        {
            Pattern = "spam@example.com",
            IsBlacklist = true,
            Note = "广告"
        });
        var whiteId = await repository.UpsertAsync(new SenderFilter
        {
            Pattern = "@good.example.com",
            IsBlacklist = false
        });

        Assert.True(blackId > 0);
        Assert.True(whiteId > 0);

        // 同一 (pattern, is_blacklist) 重复写入只更新备注，不产生新行。
        var againId = await repository.UpsertAsync(new SenderFilter
        {
            Pattern = "spam@example.com",
            IsBlacklist = true,
            Note = "广告（补充）"
        });

        Assert.Equal(blackId, againId);

        var all = await repository.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("广告（补充）", all.Single(item => item.Id == blackId).Note);
        Assert.False(all.Single(item => item.Id == whiteId).IsBlacklist);

        Assert.True(await repository.DeleteAsync(whiteId));
        Assert.Single(await repository.GetAllAsync());
        Assert.False(await repository.DeleteAsync(whiteId));
    }

    [Fact]
    public async Task Initialize_升级后_标签与待办可用()
    {
        var factory = CreateFactory();
        new DatabaseInitializer(factory).Initialize();

        var tags = new SqliteMailTagRepository(factory);
        var tagId = await tags.UpsertAsync(new MailTag { Name = "订舱", Color = "#2563eb", AccountId = 1, ShortcutKey = "1" });
        await tags.AddToMessageAsync(42, tagId);

        var byTag = await tags.GetMessageIdsAsync(tagId);
        Assert.Contains(42L, byTag);
        var forMessage = await tags.GetForMessageAsync(42);
        Assert.Equal("订舱", Assert.Single(forMessage).Name);
        Assert.Equal("1", Assert.Single(forMessage).ShortcutDisplay);

        var todos = new SqliteMailTodoRepository(factory);
        var todoId = await todos.UpsertAsync(new MailTodo
        {
            EmailId = 42,
            Title = "确认补料",
            DueUtc = DateTimeOffset.Now.AddHours(-1)
        });
        Assert.Equal(1, await todos.CountOpenAsync());
        Assert.Equal(1, await todos.CountOverdueAsync());
        Assert.True((await todos.GetByEmailAsync(42))!.IsOverdue);

        // 到期提醒：首次可取到，提醒后不再重复出现；重置后再次可取
        var due = await todos.GetDueForReminderAsync(DateTimeOffset.Now);
        Assert.Single(due);
        await todos.SetRemindedAsync(todoId, DateTimeOffset.UtcNow);
        Assert.Empty(await todos.GetDueForReminderAsync(DateTimeOffset.Now));
        await todos.SetRemindedAsync(todoId, null);
        Assert.Single(await todos.GetDueForReminderAsync(DateTimeOffset.Now));

        await todos.SetStateAsync(todoId, MailTodoState.Done);
        Assert.Equal(0, await todos.CountOpenAsync());
        Assert.Empty(await todos.GetDueForReminderAsync(DateTimeOffset.Now));
    }

    [Fact]
    public async Task Initialize_升级后_邮件备注可读写且空值即删除()
    {
        var factory = CreateFactory();
        new DatabaseInitializer(factory).Initialize();

        var notes = new SqliteMessageNoteRepository(factory);
        Assert.Equal(string.Empty, await notes.GetAsync(101));

        await notes.SaveAsync(101, "  等客户回签，周四前跟进  ");
        Assert.Equal("等客户回签，周四前跟进", await notes.GetAsync(101));

        var set = await notes.GetNotedMessageIdsAsync(new long[] { 101, 102 });
        Assert.Contains(101L, set);
        Assert.DoesNotContain(102L, set);

        // 置空等于删除，避免列表标记残留
        await notes.SaveAsync(101, "   ");
        Assert.Equal(string.Empty, await notes.GetAsync(101));
        Assert.Empty(await notes.GetNotedMessageIdsAsync(new long[] { 101 }));
    }
    [Fact]
    public void Initialize_版本4库_修复被误解为俄文的GBK主题()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var factory = CreateFactory();
        new DatabaseInitializer(factory).Initialize();

        const string expected = "回复：放舱通知";
        var malformed = Encoding.GetEncoding(1251).GetString(Encoding.GetEncoding("GB18030").GetBytes(expected));
        using (var connection = factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
INSERT INTO accounts(name, email, created_utc) VALUES('test', 'test@example.com', '2026-01-01T00:00:00.0000000+00:00');
INSERT INTO folders(account_id, full_name, kind) VALUES(1, 'INBOX', 1);
INSERT INTO messages(account_id, folder_id, uid, date_utc, subject) VALUES(1, 1, 1, '2026-01-01T00:00:00.0000000+00:00', @subject);
UPDATE meta SET value = '4' WHERE key = 'schema_version';
""";
            command.Parameters.AddWithValue("@subject", malformed);
            command.ExecuteNonQuery();
        }

        new DatabaseInitializer(factory).Initialize();

        using var verify = factory.Create();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT subject FROM messages WHERE id = 1;";
        Assert.Equal(expected, Convert.ToString(query.ExecuteScalar()));
    }

    [Fact]
    public void Initialize_全新库与重复执行_版本保持最新且幂等()
    {
        var factory = CreateFactory();

        new DatabaseInitializer(factory).Initialize();
        new DatabaseInitializer(factory).Initialize();

        using var connection = factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        Assert.Equal(DatabaseInitializer.SchemaVersion.ToString(), Convert.ToString(command.ExecuteScalar()));
    }

    [Fact]
    public void Initialize_版本19_清空旧组合键并把标签归到第一个账户()
    {
        var factory = CreateFactory();
        new DatabaseInitializer(factory).Initialize();

        // 模拟 v18 库：标签只有旧的整数编码 / 组合键文本，没有 account_id 与 shortcut_key。
        using (var connection = factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
INSERT INTO accounts(name, email, created_utc, sort_order) VALUES('工作', 'ops@forwarder.cn', '2026-10-01T00:00:00Z', 0);
UPDATE meta SET value = '18' WHERE key = 'schema_version';
INSERT INTO mail_tags(name, color, shortcut, shortcut_gesture, sort_order) VALUES('订舱', '#2563eb', 1, 'Ctrl+1', 0);
INSERT INTO mail_tags(name, color, shortcut, shortcut_gesture, sort_order) VALUES('报关', '#0d9488', 11, 'Ctrl+Shift+1', 1);
INSERT INTO mail_tags(name, color, shortcut, shortcut_gesture, sort_order) VALUES('未绑定', '#dc2626', NULL, NULL, 2);
""";
            command.ExecuteNonQuery();
        }

        new DatabaseInitializer(factory).Initialize();

        using var verify = factory.Create();
        long accountId;
        using (var accounts = verify.CreateCommand())
        {
            accounts.CommandText = "SELECT id FROM accounts LIMIT 1;";
            accountId = Convert.ToInt64(accounts.ExecuteScalar());
        }

        using var query = verify.CreateCommand();
        query.CommandText = "SELECT name, shortcut_gesture, shortcut_key, account_id FROM mail_tags ORDER BY sort_order;";
        using var reader = query.ExecuteReader();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(reader.Read());
            // 旧组合键语义已作废：迁移统一清空，单键留给用户在设置里重新选
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
            Assert.Equal(accountId, reader.GetInt64(3));
        }

        Assert.False(reader.Read());
    }

    [Fact]
    public void Initialize_版本20_账户收取策略默认继承()
    {
        var factory = CreateFactory();
        new DatabaseInitializer(factory).Initialize();

        // 模拟 v19 库：accounts 还没有 fetch_policy 列
        using (var connection = factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
INSERT INTO accounts(name, email, created_utc, sort_order) VALUES('工作', 'ops@forwarder.cn', '2026-10-01T00:00:00Z', 0);
INSERT INTO accounts(name, email, created_utc, sort_order) VALUES('订阅', 'news@forwarder.cn', '2026-10-02T00:00:00Z', 1);
UPDATE meta SET value = '19' WHERE key = 'schema_version';
""";
            command.ExecuteNonQuery();
        }

        new DatabaseInitializer(factory).Initialize();

        using var verify = factory.Create();
        using var query = verify.CreateCommand();
        query.CommandText = "SELECT fetch_policy FROM accounts ORDER BY sort_order;";
        using var reader = query.ExecuteReader();

        // 老账户迁移后一律「继承默认」：升级前后收取行为一致
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Initialize_版本21_陌生发件人列默认为0且建好判定索引()
    {
        var factory = CreateFactory();
        new DatabaseInitializer(factory).Initialize();

        // 先造一封「升级前就存在」的来信，再把库回退成 v21 的样子：
        // 去掉 v22 新增的列与索引，并把版本号改回 21，然后重新走一次升级。
        using (var connection = factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
INSERT INTO accounts(id, name, email, created_utc) VALUES(1, '工作', 'me@example.com', '2026-10-01T00:00:00Z');
INSERT INTO folders(id, account_id, full_name, kind) VALUES(1, 1, 'INBOX', 1);
INSERT INTO messages(account_id, folder_id, uid, from_email, date_utc)
VALUES(1, 1, 7, 'old@partner.com', '2026-10-01T00:00:00.0000000+00:00');
DROP INDEX IF EXISTS ix_messages_from_email;
ALTER TABLE messages DROP COLUMN is_stranger;
UPDATE meta SET value = '21' WHERE key = 'schema_version';
""";
            command.ExecuteNonQuery();
        }

        new DatabaseInitializer(factory).Initialize();

        using var verify = factory.Create();

        using (var version = verify.CreateCommand())
        {
            version.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            Assert.Equal(DatabaseInitializer.SchemaVersion.ToString(), Convert.ToString(version.ExecuteScalar()));
        }

        using (var column = verify.CreateCommand())
        {
            column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name = 'is_stranger';";
            Assert.Equal(1L, Convert.ToInt64(column.ExecuteScalar()));
        }

        using (var legacy = verify.CreateCommand())
        {
            // 存量邮件不参与判定：升级后一律非陌生
            legacy.CommandText = "SELECT is_stranger FROM messages WHERE uid = 7;";
            Assert.Equal(0L, Convert.ToInt64(legacy.ExecuteScalar()));
        }

        using var index = verify.CreateCommand();
        index.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_messages_from_email';";
        Assert.Equal(1L, Convert.ToInt64(index.ExecuteScalar()));
    }

    private SqliteConnectionFactory CreateFactory()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        return new SqliteConnectionFactory(paths);
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
}
