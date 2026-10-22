using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>「全部搜索条件」面板的每条结构化条件（Foxmail 对齐）：各自能精确命中，也能组合。</summary>
[Trait("Category", "Integration")]
public sealed class StructuredSearchFilterTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;

    public StructuredSearchFilterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-filter-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();
        _messages = new SqliteMessageRepository(_factory);

        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO accounts(id, name, email, created_utc) VALUES (1, '测试', 'me@example.com', '2026-01-01T00:00:00.0000000+00:00');
INSERT INTO folders(id, account_id, full_name, display_name, kind) VALUES (1, 1, 'INBOX', '收件箱', 0);
INSERT INTO mail_tags(id, name, account_id) VALUES (7, 'JH', 1);
""";
        command.ExecuteNonQuery();
    }

    private Task<long> AddAsync(string subject, string from, string to, bool attachment, DateTimeOffset date, string body = "")
        => _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = (uint)Random.Shared.Next(1, 100000),
            Subject = subject,
            FromEmail = from,
            To = { new EmailAddress(to, "收件") },
            DateUtc = date,
            HasAttachment = attachment,
            BodyState = body.Length > 0 ? BodyState.Downloaded : BodyState.NotDownloaded,
            BodyText = body
        });

    private async Task<List<long>> QueryAsync(MessageQuery query)
    {
        var page = await _messages.QueryAsync(query);
        return page.Items.Select(item => item.Id).ToList();
    }

    [Fact]
    public async Task 收件人与主题与全文_各自与组合都能命中()
    {
        var toMatch = await AddAsync("订舱确认 SO123", "a@carrier.com", "ops@customer.com", false, DateTimeOffset.UtcNow);
        var subjectMatch = await AddAsync("SI CUT-OFF 提醒", "b@carrier.com", "other@customer.com", false, DateTimeOffset.UtcNow);
        var bodyMatch = await AddAsync("普通邮件", "c@carrier.com", "x@customer.com", false, DateTimeOffset.UtcNow, "请查收 HBL998877 的补料");

        Assert.Equal(new[] { toMatch }, await QueryAsync(new MessageQuery { ToText = "ops@customer" }));
        Assert.Equal(new[] { subjectMatch }, await QueryAsync(new MessageQuery { SubjectText = "CUT-OFF" }));
        Assert.Equal(new[] { bodyMatch }, await QueryAsync(new MessageQuery { BodyText = "HBL998877" }));

        // 组合：发件人 + 收件人 同时命中的只有一封
        var combined = await QueryAsync(new MessageQuery { ToText = "ops@customer", SubjectText = "SO123" });
        Assert.Equal(new[] { toMatch }, combined);
    }

    [Fact]
    public async Task 附件有无需能筛选()
    {
        var withFile = await AddAsync("有附件", "a@x.com", "me@x.com", true, DateTimeOffset.UtcNow);
        var withoutFile = await AddAsync("无附件", "a@x.com", "me@x.com", false, DateTimeOffset.UtcNow);

        Assert.Equal(new[] { withFile }, await QueryAsync(new MessageQuery { HasAttachment = true }));
        Assert.Equal(new[] { withoutFile }, await QueryAsync(new MessageQuery { HasAttachment = false }));
    }

    [Fact]
    public async Task 标签与提醒的有无都能筛选()
    {
        var tagged = await AddAsync("有标签", "a@x.com", "me@x.com", false, DateTimeOffset.UtcNow);
        var plain = await AddAsync("无标签", "a@x.com", "me@x.com", false, DateTimeOffset.UtcNow);

        using (var connection = _factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
INSERT INTO message_tags(email_id, tag_id, created_utc) VALUES (@id, 7, '2026-01-01T00:00:00+00:00');
INSERT INTO mail_followups(email_id, thread_id, subject, from_email, sent_utc, recipients, next_check_utc, state, created_utc, updated_utc)
VALUES (@id, '', '等待', 'a@x.com', '2026-01-01T00:00:00+00:00', '[]', '2026-01-02T00:00:00+00:00', 0, '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00');
""";
            command.Parameters.AddWithValue("@id", tagged);
            command.ExecuteNonQuery();
        }

        Assert.Equal(new[] { tagged }, await QueryAsync(new MessageQuery { HasAnyTag = true }));
        Assert.Equal(new[] { plain }, await QueryAsync(new MessageQuery { HasAnyTag = false }));
        Assert.Equal(new[] { tagged }, await QueryAsync(new MessageQuery { TagId = 7 }));
        Assert.Equal(new[] { tagged }, await QueryAsync(new MessageQuery { HasReminder = true }));
        Assert.Equal(new[] { plain }, await QueryAsync(new MessageQuery { HasReminder = false }));
    }

    [Fact]
    public async Task 备注与时间范围_按备注文本与日期筛选()
    {
        var old = await AddAsync("旧邮件", "a@x.com", "me@x.com", false, DateTimeOffset.UtcNow.AddDays(-40));
        var recent = await AddAsync("新邮件", "a@x.com", "me@x.com", false, DateTimeOffset.UtcNow);

        using (var connection = _factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO message_notes(email_id, note, updated_utc) VALUES (@id, '客户已确认运费', '2026-01-01T00:00:00+00:00');";
            command.Parameters.AddWithValue("@id", recent);
            command.ExecuteNonQuery();
        }

        Assert.Equal(new[] { recent }, await QueryAsync(new MessageQuery { NoteText = "运费" }));
        Assert.Equal(new[] { recent }, await QueryAsync(new MessageQuery { SinceUtc = DateTimeOffset.UtcNow.AddDays(-3) }));
        Assert.Equal(new[] { old }, await QueryAsync(new MessageQuery { BeforeUtc = DateTimeOffset.UtcNow.AddDays(-3) }));
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
        }
    }
}
