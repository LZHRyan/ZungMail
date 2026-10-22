using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Configuration;
using ZungMail.Infrastructure.Storage;
using ZungMail.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 发件箱仓储（FR-08 定时发送）的失败退避验证：后台调度器每隔数十秒扫描一次，
/// 必须依靠 next_attempt_utc 推迟重试，否则会在极短时间内耗尽全部自动重试次数。
/// </summary>
[Trait("Category", "Integration")]
public sealed class OutboxRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteOutboxRepository _outbox;

    public OutboxRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-outbox-" + Guid.NewGuid().ToString("N")[..8]);
        _factory = CreateFactory(_root);
        _outbox = new SqliteOutboxRepository(_factory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响验证结论
        }
    }

    [Fact]
    public async Task GetDueAsync_退避未到时_不返回_到点后重新返回()
    {
        var id = await AddAsync();
        var now = DateTimeOffset.UtcNow;

        await _outbox.UpdateStateAsync(id, OutboxState.Failed, "SMTP 超时", 1, now.AddMinutes(1));

        Assert.Empty(await _outbox.GetDueAsync(now));
        Assert.Single(await _outbox.GetDueAsync(now.AddMinutes(1)));
    }

    [Fact]
    public async Task GetDueAsync_用尽自动重试次数后_不再返回()
    {
        var id = await AddAsync();

        await _outbox.UpdateStateAsync(id, OutboxState.Failed, "SMTP 超时", OutboxItem.MaxAttempts, null);

        Assert.Empty(await _outbox.GetDueAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task GetDueAsync_定时未到不返回_到点后返回()
    {
        var now = DateTimeOffset.UtcNow;
        await AddAsync(now.AddMinutes(5));

        Assert.Empty(await _outbox.GetDueAsync(now));
        Assert.Single(await _outbox.GetDueAsync(now.AddMinutes(5)));
    }

    [Fact]
    public async Task UpdateStateAsync_重置为待发送_清除退避时间()
    {
        var id = await AddAsync();
        var now = DateTimeOffset.UtcNow;

        await _outbox.UpdateStateAsync(id, OutboxState.Failed, "SMTP 超时", 3, now.AddMinutes(30));
        Assert.Empty(await _outbox.GetDueAsync(now));

        // 用户手动重试：次数归零、退避清除，应立刻可投递
        await _outbox.UpdateStateAsync(id, OutboxState.Pending, null, 0);

        var due = await _outbox.GetDueAsync(now);
        Assert.Equal(id, Assert.Single(due).Id);
    }

    [Fact]
    public async Task 读取条目_带回退时间()
    {
        var id = await AddAsync();
        var next = DateTimeOffset.UtcNow.AddMinutes(4);

        await _outbox.UpdateStateAsync(id, OutboxState.Failed, "SMTP 超时", 2, next);

        var item = await _outbox.GetAsync(id);
        Assert.NotNull(item);
        Assert.Equal(OutboxState.Failed, item!.State);
        Assert.Equal(2, item.Attempts);
        Assert.Equal(next, item.NextAttemptUtc);
    }

    [Fact]
    public async Task AddAsync_生成稳定MessageId_状态更新后不变()
    {
        var item = new OutboxItem { AccountId = 1, Subject = "Message-ID 验证" };
        var id = await _outbox.AddAsync(item);

        Assert.False(string.IsNullOrWhiteSpace(item.MessageId));
        Assert.EndsWith("@zungmail.local", item.MessageId);

        await _outbox.UpdateStateAsync(id, OutboxState.Failed, "SMTP 超时", 1);

        var reloaded = await _outbox.GetAsync(id);
        Assert.Equal(item.MessageId, reloaded!.MessageId);
    }

    [Fact]
    public async Task RecoverInterruptedAsync_残留发送中标记为结果未知且不自动重发()
    {
        var id = await AddAsync();
        await _outbox.UpdateStateAsync(id, OutboxState.Sending, null, 2);

        // 回收只依赖 outbox 仓储，其余依赖在本次场景中不会被访问。
        var processor = new OutboxProcessor(
            _outbox, null!, null!, null!, null!, NullLogger<OutboxProcessor>.Instance);

        var recovered = await processor.RecoverInterruptedAsync();

        Assert.Equal(1, recovered);
        var item = await _outbox.GetAsync(id);
        Assert.Equal(OutboxState.Uncertain, item!.State);
        Assert.Contains("发送结果未知", item.LastError);
        Assert.Empty(await _outbox.GetDueAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Initialize_旧库缺少退避列时_自动补齐()
    {
        var legacyRoot = Path.Combine(Path.GetTempPath(), "zungmail-legacy-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var paths = new AppPaths(legacyRoot);
            paths.EnsureCreated();
            var factory = new SqliteConnectionFactory(paths);

            using (var connection = factory.Create())
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "CREATE TABLE outbox(id INTEGER PRIMARY KEY AUTOINCREMENT, account_id INTEGER NOT NULL, created_utc TEXT NOT NULL);";
                command.ExecuteNonQuery();
            }

            new DatabaseInitializer(factory).Initialize();

            using var verify = factory.Create();
            using var check = verify.CreateCommand();
            check.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('outbox') WHERE name IN ('next_attempt_utc', 'message_id');";
            Assert.Equal(2L, Convert.ToInt64(check.ExecuteScalar()));
        }
        finally
        {
            try
            {
                if (Directory.Exists(legacyRoot)) Directory.Delete(legacyRoot, true);
            }
            catch (IOException)
            {
                // 忽略清理失败
            }
        }
    }

    private static SqliteConnectionFactory CreateFactory(string root)
    {
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(factory).Initialize();
        return factory;
    }

    private async Task<long> AddAsync(DateTimeOffset? scheduledUtc = null) => await _outbox.AddAsync(new OutboxItem
    {
        AccountId = 1,
        Subject = "定时发送验证",
        ScheduledUtc = scheduledUtc
    });
}
