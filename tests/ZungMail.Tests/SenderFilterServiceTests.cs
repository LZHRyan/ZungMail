using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>黑白名单服务：输入校验、持久化往返与命中缓存失效。</summary>
[Trait("Category", "Unit")]
public class SenderFilterServiceTests
{
    [Fact]
    public async Task 保存_规范化后写入仓库()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);

        var result = await service.SaveAsync("  Boss@Example.COM ", isBlacklist: true, note: " 客户 ");

        Assert.True(result.Success, result.Message);
        var saved = Assert.Single(repository.Items);
        Assert.Equal("boss@example.com", saved.Pattern);
        Assert.True(saved.IsBlacklist);
        Assert.Equal("客户", saved.Note);
    }

    [Fact]
    public async Task 保存_非法输入返回失败说明且不落库()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);

        var result = await service.SaveAsync("这不是邮箱", isBlacklist: true);

        Assert.False(result.Success);
        Assert.Contains("boss@example.com", result.Message);
        Assert.Empty(repository.Items);
    }

    [Fact]
    public async Task 判定_写入后立即可用且白名单优先()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);

        await service.SaveAsync("@spam.example", isBlacklist: true);
        await service.SaveAsync("boss@spam.example", isBlacklist: false);

        Assert.Equal(SenderVerdict.Blacklisted, await service.ClassifyAsync("ads@spam.example"));
        Assert.Equal(SenderVerdict.Whitelisted, await service.ClassifyAsync("boss@spam.example"));
        Assert.Equal(SenderVerdict.None, await service.ClassifyAsync("ops@ship.example"));
        Assert.Equal(SenderVerdict.None, await service.ClassifyAsync(""));
    }

    [Fact]
    public async Task 判定_删除后缓存立即失效()
    {
        var repository = new FakeRepository();
        var service = CreateService(repository);

        var result = await service.SaveAsync("ads@spam.example", isBlacklist: true);
        Assert.Equal(SenderVerdict.Blacklisted, await service.ClassifyAsync("ads@spam.example"));

        Assert.True(await service.DeleteAsync(result.Filter!.Id));
        Assert.Equal(SenderVerdict.None, await service.ClassifyAsync("ads@spam.example"));
    }

    [Fact]
    public async Task 判定_仓库异常时按未命中处理()
    {
        var service = CreateService(new FakeRepository { FailOnRead = true });

        Assert.Equal(SenderVerdict.None, await service.ClassifyAsync("ads@spam.example"));
    }

    private static SenderFilterService CreateService(ISenderFilterRepository repository)
        => new(repository, NullLogger<SenderFilterService>.Instance);

    private sealed class FakeRepository : ISenderFilterRepository
    {
        private long _nextId = 1;

        public List<SenderFilter> Items { get; } = new();

        public bool FailOnRead { get; init; }

        public Task<IReadOnlyList<SenderFilter>> GetAllAsync(CancellationToken ct = default)
        {
            if (FailOnRead) throw new InvalidOperationException("数据库不可用");
            return Task.FromResult<IReadOnlyList<SenderFilter>>(Items.ToList());
        }

        public Task<long> UpsertAsync(SenderFilter filter, CancellationToken ct = default)
        {
            var existing = Items.FirstOrDefault(
                item => item.Pattern == filter.Pattern && item.IsBlacklist == filter.IsBlacklist);
            if (existing is null)
            {
                filter.Id = _nextId++;
                Items.Add(filter);
            }
            else
            {
                existing.Note = filter.Note;
                filter.Id = existing.Id;
            }

            return Task.FromResult(filter.Id);
        }

        public Task<bool> DeleteAsync(long id, CancellationToken ct = default)
            => Task.FromResult(Items.RemoveAll(item => item.Id == id) > 0);
    }
}
