using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 标签批量写入：一次「给 N 封邮件打 M 个标签」不能再按封/按标签逐条连库，
/// 否则界面线程要等 N×M 次连接，200 封 × 5 个标签就是上千次。
/// </summary>
public sealed class MailTagBatchTests
{
    [Fact]
    public async Task 批量打标签_整批只调用一次批量写入()
    {
        var repo = new CountingTagRepository();
        var service = new MailTagService(repo);

        await service.ApplyAsync(new long[] { 1, 2, 3, 4 }, tagId: 9, add: true);

        Assert.Equal(1, repo.BatchAddCalls);
        Assert.Equal(4, repo.LastBatchCount);
        Assert.Equal(9, repo.LastTagId);
        Assert.Equal(0, repo.SingleAddCalls);
    }

    [Fact]
    public async Task 批量移除标签_整批只调用一次批量删除()
    {
        var repo = new CountingTagRepository();
        var service = new MailTagService(repo);

        await service.ApplyAsync(new long[] { 5, 6 }, tagId: 3, add: false);

        Assert.Equal(1, repo.BatchRemoveCalls);
        Assert.Equal(2, repo.LastBatchCount);
        Assert.Equal(0, repo.SingleRemoveCalls);
    }

    [Fact]
    public async Task 空邮件列表_不触发任何写操作()
    {
        var repo = new CountingTagRepository();
        var service = new MailTagService(repo);

        await service.ApplyAsync(Array.Empty<long>(), tagId: 1, add: true);

        Assert.Equal(0, repo.BatchAddCalls);
        Assert.Equal(0, repo.SingleAddCalls);
    }

    /// <summary>只统计调用次数的替身：其余成员用不到，直接抛。</summary>
    private sealed class CountingTagRepository : IMailTagRepository
    {
        public int BatchAddCalls { get; private set; }
        public int BatchRemoveCalls { get; private set; }
        public int SingleAddCalls { get; private set; }
        public int SingleRemoveCalls { get; private set; }
        public int LastBatchCount { get; private set; }
        public long LastTagId { get; private set; }

        public Task AddToMessagesAsync(long tagId, IReadOnlyList<long> emailIds, CancellationToken ct = default)
        {
            BatchAddCalls++;
            LastTagId = tagId;
            LastBatchCount = emailIds.Count;
            return Task.CompletedTask;
        }

        public Task RemoveFromMessagesAsync(long tagId, IReadOnlyList<long> emailIds, CancellationToken ct = default)
        {
            BatchRemoveCalls++;
            LastTagId = tagId;
            LastBatchCount = emailIds.Count;
            return Task.CompletedTask;
        }

        public Task AddToMessageAsync(long emailId, long tagId, CancellationToken ct = default)
        {
            SingleAddCalls++;
            return Task.CompletedTask;
        }

        public Task RemoveFromMessageAsync(long emailId, long tagId, CancellationToken ct = default)
        {
            SingleRemoveCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MailTag>> GetAllAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<MailTag>> GetByAccountAsync(long accountId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<long> UpsertAsync(MailTag tag, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(long id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<MailTag>> GetForMessageAsync(long emailId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<long, IReadOnlyList<MailTag>>> GetForMessagesAsync(
            IReadOnlyList<long> emailIds, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<long>> GetMessageIdsAsync(long tagId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
