using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZungMail.Tests;

/// <summary>待同步操作队列：同邮件去重、状态机、数量统计与成功记录清理。</summary>
[Trait("Category", "Integration")]
public sealed class PendingOperationRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly SqlitePendingOperationRepository _pending;

    public PendingOperationRepositoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-pending-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(factory).Initialize();
        _pending = new SqlitePendingOperationRepository(factory);
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

    [Fact]
    public async Task EnqueueAsync_同一邮件同类操作_只保留最新意图()
    {
        var first = await _pending.EnqueueAsync(new PendingOperation
        {
            EmailId = 42,
            Kind = PendingOperationKind.MarkSeen,
            Flags = MessageFlags.Seen
        });
        var second = await _pending.EnqueueAsync(new PendingOperation
        {
            EmailId = 42,
            Kind = PendingOperationKind.MarkSeen,
            Flags = MessageFlags.None
        });

        var outstanding = await _pending.GetOutstandingAsync();

        var operation = Assert.Single(outstanding);
        Assert.Equal(second, operation.Id);
        Assert.NotEqual(first, operation.Id);
        Assert.Equal(MessageFlags.None, operation.Flags);
    }

    [Fact]
    public async Task 状态机_失败保留在队列_成功后不再计入待同步()
    {
        var operationId = await _pending.EnqueueAsync(new PendingOperation
        {
            EmailId = 7,
            Kind = PendingOperationKind.Deleted,
            Flags = MessageFlags.Deleted
        });

        await _pending.UpdateStateAsync(operationId, PendingOperationState.Syncing);
        await _pending.UpdateStateAsync(operationId, PendingOperationState.Failed, "网络不可用", 2);

        var failed = Assert.Single(await _pending.GetOutstandingAsync());
        Assert.Equal(PendingOperationState.Failed, failed.State);
        Assert.Equal("网络不可用", failed.LastError);
        Assert.Equal(2, failed.Attempts);
        Assert.Equal(1, await _pending.CountOutstandingAsync());

        await _pending.UpdateStateAsync(operationId, PendingOperationState.Succeeded);

        Assert.Empty(await _pending.GetOutstandingAsync());
        Assert.Equal(0, await _pending.CountOutstandingAsync());
    }

    [Fact]
    public async Task 批量已读_Payload可原样读取()
    {
        const string payload = """[{"Id":1,"Flags":1},{"Id":2,"Flags":1}]""";
        var operationId = await _pending.EnqueueAsync(new PendingOperation
        {
            EmailId = 0,
            Kind = PendingOperationKind.MarkSeenBatch,
            Payload = payload
        });

        var operation = await _pending.GetAsync(operationId);

        Assert.NotNull(operation);
        Assert.Equal(PendingOperationKind.MarkSeenBatch, operation!.Kind);
        Assert.Equal(payload, operation.Payload);
    }

    [Fact]
    public async Task 移动邮件_目标文件夹Payload可持久化并保留待同步状态()
    {
        const string payload = "{\"Destination\":6}"; // FolderKind.Archive
        var operationId = await _pending.EnqueueAsync(new PendingOperation
        {
            EmailId = 99,
            Kind = PendingOperationKind.Move,
            Flags = MessageFlags.Deleted,
            Payload = payload
        });

        var operation = await _pending.GetAsync(operationId);

        Assert.NotNull(operation);
        Assert.Equal(PendingOperationKind.Move, operation!.Kind);
        Assert.Equal(MessageFlags.Deleted, operation.Flags);
        Assert.Equal(payload, operation.Payload);
        Assert.Equal(1, await _pending.CountOutstandingAsync());
    }

    [Fact]
    public async Task PruneSucceededAsync_只清理过期的成功记录()
    {
        var oldSuccess = await _pending.EnqueueAsync(new PendingOperation { EmailId = 1, Kind = PendingOperationKind.MarkSeen });
        await _pending.UpdateStateAsync(oldSuccess, PendingOperationState.Succeeded);

        var recentSuccess = await _pending.EnqueueAsync(new PendingOperation { EmailId = 2, Kind = PendingOperationKind.Flagged });
        await _pending.UpdateStateAsync(recentSuccess, PendingOperationState.Succeeded);

        var failed = await _pending.EnqueueAsync(new PendingOperation { EmailId = 3, Kind = PendingOperationKind.Deleted });
        await _pending.UpdateStateAsync(failed, PendingOperationState.Failed, "超时", 1);

        // 把第一条成功记录改为很久以前更新，模拟历史数据
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "mail.db")}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE pending_operations SET updated_utc = @old WHERE id = @id;";
            command.Parameters.AddWithValue("@old", DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
            command.Parameters.AddWithValue("@id", oldSuccess);
            await command.ExecuteNonQueryAsync();
        }

        var pruned = await _pending.PruneSucceededAsync(DateTimeOffset.UtcNow.AddDays(-7));

        Assert.Equal(1, pruned);
        Assert.Null(await _pending.GetAsync(oldSuccess));
        Assert.NotNull(await _pending.GetAsync(recentSuccess));
        Assert.NotNull(await _pending.GetAsync(failed));
    }
}
