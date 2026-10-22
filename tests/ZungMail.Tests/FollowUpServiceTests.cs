using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 等待回复闭环：谁回了、还差谁、到点提醒一次、满足条件自动结束。
/// 回复判定复用会话内入站邮件，不做额外的回复表。
/// </summary>
[Trait("Category", "Integration")]
public sealed class FollowUpServiceTests : IDisposable
{
    private const string Thread = "<thread-followup-1@example>";
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;
    private readonly SqliteMailFollowUpRepository _repository;
    private readonly MailFollowUpService _service;

    public FollowUpServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-followup-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();
        _messages = new SqliteMessageRepository(_factory);
        _repository = new SqliteMailFollowUpRepository(_factory);
        _service = new MailFollowUpService(_repository, NullLogger<MailFollowUpService>.Instance);
        SeedAccountAndFolder();
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

    private Task<long> AddMessageAsync(uint uid, string fromEmail, DateTimeOffset date, string subject = "SO No.: SHA1234567")
        => _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = uid,
            MessageId = $"<msg-{uid}@example>",
            ThreadId = Thread,
            Subject = subject,
            FromEmail = fromEmail,
            FromName = fromEmail,
            DateUtc = date
        });

    private static EmailDetail OutgoingSource(long id) => new()
    {
        Id = id,
        AccountId = 1,
        FolderId = 2,
        Uid = 100,
        MessageId = "<source@example>",
        ThreadId = Thread,
        Subject = "SI request for SO SHA1234567",
        FromEmail = "me@example.com",
        FromName = "Me",
        DateUtc = BaseTime,
        To = new List<EmailAddress>
        {
            new("ops-a@customer.example", "A"),
            new("ops-b@customer.example", "B")
        },
        Cc = new List<EmailAddress> { new("boss@customer.example", "Boss") }
    };

    private Task<(bool Ok, string? Error, MailFollowUp? FollowUp)> CreateAllRequiredAsync(long sourceId, bool requireAll = true, int minReplies = 1)
        => _service.CreateAsync(
            OutgoingSource(sourceId),
            new[] { "ops-a@customer.example", "ops-b@customer.example" },
            requireAll,
            minReplies,
            includeCc: false,
            nextCheckUtc: DateTimeOffset.Now.AddHours(4),
            note: "等客户补料",
            selfAddress: "me@example.com");

    [Fact]
    public async Task 全部回复_自动满足并结束()
    {
        var source = await AddMessageAsync(1, "me@example.com", BaseTime);
        var created = await CreateAllRequiredAsync(source);
        Assert.True(created.Ok, created.Error);

        await AddMessageAsync(2, "ops-a@customer.example", BaseTime.AddHours(1));
        await AddMessageAsync(3, "ops-b@customer.example", BaseTime.AddHours(2));

        var status = await _service.GetStatusAsync(created.FollowUp!.Id);
        Assert.NotNull(status);
        Assert.True(status!.IsSatisfied);
        Assert.Empty(status.Pending);
        Assert.Equal(2, status.RepliedCount);
        Assert.Equal("2/2 已回复", status.ProgressText);

        var stored = await _repository.GetAsync(created.FollowUp.Id);
        Assert.Equal(FollowUpState.Satisfied, stored!.State);
    }

    [Fact]
    public async Task 部分回复_列出未回复的人()
    {
        var source = await AddMessageAsync(1, "me@example.com", BaseTime);
        var created = await CreateAllRequiredAsync(source);
        await AddMessageAsync(2, "ops-a@customer.example", BaseTime.AddHours(1));

        var status = await _service.GetStatusAsync(created.FollowUp!.Id);
        Assert.False(status!.IsSatisfied);
        Assert.Equal(new[] { "ops-b@customer.example" }, status.Pending);
        Assert.Contains("还差", status.PendingText);
    }

    [Fact]
    public async Task 至少一人回复_满足条件()
    {
        var source = await AddMessageAsync(1, "me@example.com", BaseTime);
        var created = await CreateAllRequiredAsync(source, requireAll: false, minReplies: 1);
        await AddMessageAsync(2, "ops-a@customer.example", BaseTime.AddHours(1));

        var status = await _service.GetStatusAsync(created.FollowUp!.Id);
        Assert.True(status!.IsSatisfied);
    }

    [Fact]
    public async Task 到点提醒一次_不会重复轰炸()
    {
        var source = await AddMessageAsync(1, "me@example.com", BaseTime);
        var created = await _service.CreateAsync(
            OutgoingSource(source),
            new[] { "ops-a@customer.example" },
            requireAll: true,
            minReplies: 1,
            includeCc: false,
            nextCheckUtc: DateTimeOffset.Now.AddMinutes(-5),
            note: "");
        Assert.True(created.Ok, created.Error);

        var first = await _service.CheckDueAsync(DateTimeOffset.Now);
        Assert.Single(first);
        Assert.Equal("ops-a@customer.example", Assert.Single(first[0].Pending));

        // 已提醒 → 不再进入到期队列，直到用户「继续跟进」
        var second = await _service.CheckDueAsync(DateTimeOffset.Now);
        Assert.Empty(second);

        var stored = await _repository.GetAsync(created.FollowUp!.Id);
        Assert.NotNull(stored!.NotifiedUtc);
        Assert.Equal(FollowUpState.Waiting, stored.State);

        await _service.RescheduleAsync(stored.Id, DateTimeOffset.Now.AddMinutes(-1));
        var afterResume = await _service.CheckDueAsync(DateTimeOffset.Now);
        Assert.Single(afterResume);
    }

    [Fact]
    public async Task 已满足的跟进_到期时不提醒()
    {
        var source = await AddMessageAsync(1, "me@example.com", BaseTime);
        var created = await _service.CreateAsync(
            OutgoingSource(source),
            new[] { "ops-a@customer.example" },
            requireAll: true,
            minReplies: 1,
            includeCc: false,
            nextCheckUtc: DateTimeOffset.Now.AddMinutes(-5),
            note: "");
        await AddMessageAsync(2, "ops-a@customer.example", BaseTime.AddHours(1));

        var due = await _service.CheckDueAsync(DateTimeOffset.Now);
        Assert.Empty(due);

        var stored = await _repository.GetAsync(created.FollowUp!.Id);
        Assert.Equal(FollowUpState.Satisfied, stored!.State);
        Assert.Null(stored.NotifiedUtc);
    }

    [Fact]
    public async Task 没有收件人_拒绝创建()
    {
        var source = await AddMessageAsync(1, "me@example.com", BaseTime);
        var result = await _service.CreateAsync(
            OutgoingSource(source),
            Array.Empty<string>(),
            requireAll: true,
            minReplies: 1,
            includeCc: false,
            nextCheckUtc: DateTimeOffset.Now.AddHours(1),
            note: "");

        Assert.False(result.Ok);
        Assert.Contains("联系人", result.Error);
    }

    [Fact]
    public async Task 同一封邮件_只允许一条进行中的跟进()
    {
        var source = await AddMessageAsync(1, "me@example.com", BaseTime);
        var first = await CreateAllRequiredAsync(source);
        var second = await CreateAllRequiredAsync(source);

        Assert.True(first.Ok, first.Error);
        Assert.False(second.Ok);
        Assert.Contains("已有一条", second.Error);
        Assert.Equal(1, await _service.CountActiveAsync());
    }

    [Fact]
    public async Task 删除原邮件_跟进级联清理()
    {
        var source = await AddMessageAsync(1, "me@example.com", BaseTime);
        var created = await CreateAllRequiredAsync(source);
        Assert.Equal(1, await _service.CountActiveAsync());

        await _messages.DeleteAsync(source);

        Assert.Null(await _repository.GetAsync(created.FollowUp!.Id));
        Assert.Equal(0, await _service.CountActiveAsync());
    }

    [Fact]
    public async Task 收件人规范化_去重并忽略自己()
    {
        var source = await AddMessageAsync(1, "me@example.com", BaseTime);
        var result = await _service.CreateAsync(
            OutgoingSource(source),
            new[] { " OPS-A@customer.example ", "ops-a@customer.example", "me@example.com", "not-an-address" },
            requireAll: true,
            minReplies: 1,
            includeCc: false,
            nextCheckUtc: DateTimeOffset.Now.AddHours(1),
            note: "",
            selfAddress: "ME@example.com");

        Assert.True(result.Ok, result.Error);
        var stored = await _repository.GetAsync(result.FollowUp!.Id);
        var recipient = Assert.Single(stored!.Recipients);
        Assert.Equal("ops-a@customer.example", recipient);
    }
}
