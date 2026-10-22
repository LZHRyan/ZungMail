using System.Diagnostics;
using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CoreFlags = ZungMail.Core.Domain.MessageFlags;

namespace ZungMail.Tests;

/// <summary>
/// 批量移动拆成「本地生效」+「后台回写」两段后的契约：
/// 本地这一段必须是毫秒级（否则界面还是要等），后台那一段才碰网络。
/// </summary>
[Trait("Category", "Integration")]
public sealed class BatchMovePipelineTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;
    private readonly SqlitePendingOperationRepository _pending;
    private readonly SqliteFolderRepository _folders;
    private readonly StubSync _sync = new();
    private readonly MailActionService _actions;

    public BatchMovePipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-move-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();

        _messages = new SqliteMessageRepository(_factory);
        _pending = new SqlitePendingOperationRepository(_factory);
        _folders = new SqliteFolderRepository(_factory);
        _actions = new MailActionService(_messages, _pending, _folders, _sync, NullLogger<MailActionService>.Instance);

        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO accounts(id, name, email, created_utc) VALUES (1, '测试', 'me@example.com', '2026-01-01T00:00:00.0000000+00:00');
INSERT INTO folders(id, account_id, full_name, display_name, kind) VALUES (1, 1, 'INBOX', '收件箱', 0);
INSERT INTO folders(id, account_id, full_name, display_name, kind) VALUES (2, 1, 'Archive', '归档', 3);
""";
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task 本地生效_50封只用毫秒级且入队完整_后台回写后清空本地行()
    {
        var rows = new List<EmailSummary>();
        for (var i = 0; i < 50; i++)
        {
            var id = await _messages.UpsertAsync(new EmailDetail
            {
                AccountId = 1,
                FolderId = 1,
                Uid = (uint)(i + 1),
                Subject = $"邮件 {i}",
                FromEmail = "a@example.com",
                DateUtc = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
            rows.Add((await _messages.GetDetailAsync(id))!);
        }

        var destination = await _folders.GetAsync(2);

        // ① 本地部分：UI 线程只需要等这一段
        var watch = Stopwatch.StartNew();
        var handle = await _actions.PrepareMoveToFolderBatchAsync(rows, destination!);
        watch.Stop();

        Assert.Equal(50, handle.Messages.Count);
        Assert.Equal(50, handle.Operations.Count);
        Assert.True(watch.ElapsedMilliseconds < 300, $"本地生效耗时 {watch.ElapsedMilliseconds}ms，超过 300ms 的体感阈值");
        Assert.Equal(50, await _pending.CountOutstandingAsync());
        Assert.Equal(0, await _messages.CountAsync(folderId: 1)); // 列表口径已看不到这些邮件（\Deleted）
        Assert.Equal(0, _sync.BatchCalls);

        // ② 后台部分：才碰服务器
        var result = await _actions.PushMoveToFolderBatchAsync(handle);

        Assert.Equal(1, _sync.BatchCalls);
        Assert.Equal(50, result.Total);
        Assert.Equal(50, result.RemoteConfirmed);
        Assert.Equal(0, await _pending.CountOutstandingAsync());
        Assert.Equal(0, await _messages.CountAsync());
    }

    private sealed class StubSync : IMailSyncService
    {
        public int BatchCalls { get; private set; }

        public event EventHandler<SyncProgress>? ProgressChanged { add { } remove { } }

        public Task<IReadOnlyDictionary<long, MovePushResult>> MoveToFolderBatchAsync(
            IReadOnlyList<long> emailIds, long destinationAccountId, string destinationFullName, CancellationToken ct = default)
        {
            BatchCalls++;
            return Task.FromResult<IReadOnlyDictionary<long, MovePushResult>>(
                emailIds.ToDictionary(id => id, _ => MovePushResult.Moved));
        }

        public Task<SyncOutcome> SyncAccountAsync(long accountId, bool fullResync = false, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SyncOutcome>> SyncAllAsync(bool fullResync = false, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<SyncOutcome>> SyncAllIncrementalAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SyncOutcome> SyncInboxAsync(long accountId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> TestConnectionAsync(Account account, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> DownloadBodyAsync(long emailId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> DownloadBodyAsync(long emailId, bool includeAttachments, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> RedownloadBodyAsync(long emailId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> PushFlagsAsync(long emailId, CoreFlags flags, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<int> PushFlagsBatchAsync(IReadOnlyList<(long EmailId, CoreFlags Flags)> updates, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MovePushResult> MoveAsync(long emailId, FolderKind destination, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MovePushResult> MoveToFolderAsync(long emailId, long destinationAccountId, string destinationFullName, CancellationToken ct = default)
            => throw new NotSupportedException();
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
