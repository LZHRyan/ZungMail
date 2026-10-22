using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>单账户手动同步：只跑指定账户，并与全账户同步共用同一道互斥门。</summary>
[Trait("Category", "Unit")]
public sealed class SynchronizationCoordinatorTests
{
    private static SynchronizationCoordinator CreateCoordinator(FakeSyncService sync)
        => new(sync, new SyncExecutionGate(), NullLogger<SynchronizationCoordinator>.Instance);

    [Fact]
    public async Task 单账户同步_只调用该账户且不触发全账户同步()
    {
        var sync = new FakeSyncService();
        var coordinator = CreateCoordinator(sync);

        var outcome = await coordinator.SyncAccountAsync(42);

        Assert.Equal(1, sync.AccountCalls);
        Assert.Equal(42, sync.LastAccountId);
        Assert.Equal(0, sync.AllCalls);
        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.NewMessages);
    }

    [Fact]
    public async Task 单账户同步_等待后台轮询释放同步门()
    {
        var sync = new FakeSyncService();
        var coordinator = CreateCoordinator(sync);

        // 后台一轮同步先占住门（模拟定时轮询正在跑）
        var background = coordinator.SyncIncrementAsync();
        await sync.BackgroundEntered;

        var single = coordinator.SyncAccountAsync(7);
        await Task.Delay(80);
        Assert.Equal(0, sync.AccountCalls);

        sync.ReleaseBackground();
        await background;
        await single;

        Assert.Equal(1, sync.AccountCalls);
        Assert.Equal(7, sync.LastAccountId);
    }

    /// <summary>只实现同步相关入口，其余接口成员由协调器测试用不到。</summary>
    private sealed class FakeSyncService : IMailSyncService
    {
        private readonly TaskCompletionSource _backgroundEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _backgroundRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task BackgroundEntered => _backgroundEntered.Task;

        public void ReleaseBackground() => _backgroundRelease.TrySetResult();

        public int AccountCalls { get; private set; }

        public int AllCalls { get; private set; }

        public long? LastAccountId { get; private set; }

        public event EventHandler<SyncProgress>? ProgressChanged
        {
            add { }
            remove { }
        }

        public Task<SyncOutcome> SyncAccountAsync(long accountId, bool fullResync = false, CancellationToken ct = default)
        {
            AccountCalls++;
            LastAccountId = accountId;
            return Task.FromResult(new SyncOutcome
            {
                AccountId = accountId,
                AccountName = "测试账户",
                FoldersSynced = 3,
                NewMessages = 2
            });
        }

        public Task<IReadOnlyList<SyncOutcome>> SyncAllAsync(bool fullResync = false, CancellationToken ct = default)
        {
            AllCalls++;
            return Task.FromResult<IReadOnlyList<SyncOutcome>>(Array.Empty<SyncOutcome>());
        }

        public async Task<IReadOnlyList<SyncOutcome>> SyncAllIncrementalAsync(CancellationToken ct = default)
        {
            AllCalls++;
            _backgroundEntered.TrySetResult();
            await _backgroundRelease.Task;
            return Array.Empty<SyncOutcome>();
        }

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

        public Task<bool> PushFlagsAsync(long emailId, MessageFlags flags, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> PushFlagsBatchAsync(IReadOnlyList<(long EmailId, MessageFlags Flags)> updates, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MovePushResult> MoveAsync(long emailId, FolderKind destination, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MovePushResult> MoveToFolderAsync(long emailId, long destinationAccountId, string destinationFullName, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<long, MovePushResult>> MoveToFolderBatchAsync(
            IReadOnlyList<long> emailIds, long destinationAccountId, string destinationFullName, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
