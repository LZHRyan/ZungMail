using ZungMail.Core.Domain;
using ZungMail.Mail;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 正文下载协调器：同一封只下一次（单飞）、完成后可重试、交互计数驱动「预取让路」。
/// 这几条决定了「用户点开一封邮件」会不会和后台预取重复下载、会不会堆出多条连接。
/// </summary>
[Trait("Category", "Unit")]
public sealed class BodyDownloadCoordinatorTests
{
    private static EmailDetail Detail(long id) => new() { Id = id, BodyState = BodyState.Downloaded };

    [Fact]
    public async Task 同一封并发请求只启动一次下载()
    {
        var coordinator = new BodyDownloadCoordinator();
        var gate = new TaskCompletionSource<EmailDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<EmailDetail> Start()
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        }

        var first = coordinator.GetOrStart(42, Start);
        var second = coordinator.GetOrStart(42, Start);

        Assert.Same(first, second);
        Assert.Equal(1, calls);

        gate.SetResult(Detail(42));

        Assert.Equal(42, (await first).Id);
        Assert.Equal(42, (await second).Id);
    }

    [Fact]
    public async Task 下载完成后从在飞表移除以便下次重试()
    {
        var coordinator = new BodyDownloadCoordinator();
        var calls = 0;

        Task<EmailDetail> Start()
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Detail(7));
        }

        await coordinator.GetOrStart(7, Start);

        Assert.Equal(1, calls);
        Assert.Equal(0, coordinator.InFlightCount);

        // 再次请求会重新启动（例如正文被清理后要重新拉）
        await coordinator.GetOrStart(7, Start);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 下载失败也会移除并在下次重试()
    {
        var coordinator = new BodyDownloadCoordinator();
        var calls = 0;

        Task<EmailDetail> Fail()
        {
            Interlocked.Increment(ref calls);
            return Task.FromException<EmailDetail>(new IOException("模拟网络中断"));
        }

        await Assert.ThrowsAsync<IOException>(() => coordinator.GetOrStart(9, Fail));

        Assert.Equal(1, calls);
        Assert.Equal(0, coordinator.InFlightCount);
    }

    [Fact]
    public void 没有在飞任务时查询返回false()
    {
        var coordinator = new BodyDownloadCoordinator();

        Assert.False(coordinator.TryGet(1, out _));
    }

    [Fact]
    public void 交互计数随作用域增减()
    {
        var coordinator = new BodyDownloadCoordinator();
        Assert.False(coordinator.HasInteractiveRequests);

        var scope = coordinator.EnterInteractive();
        Assert.True(coordinator.HasInteractiveRequests);

        // 嵌套登记：释放一次仍然有人在等
        var nested = coordinator.EnterInteractive();
        nested.Dispose();
        Assert.True(coordinator.HasInteractiveRequests);

        scope.Dispose();
        Assert.False(coordinator.HasInteractiveRequests);

        // 重复释放不会把计数减成负数
        nested.Dispose();
        Assert.False(coordinator.HasInteractiveRequests);
    }

    [Fact]
    public async Task 不同邮件各自并行下载()
    {
        var coordinator = new BodyDownloadCoordinator();
        var firstGate = new TaskCompletionSource<EmailDetail>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource<EmailDetail>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.GetOrStart(1, () => firstGate.Task);
        var second = coordinator.GetOrStart(2, () => secondGate.Task);

        Assert.Equal(2, coordinator.InFlightCount);

        secondGate.SetResult(Detail(2));
        Assert.Equal(2, (await second).Id);

        firstGate.SetResult(Detail(1));
        Assert.Equal(1, (await first).Id);
    }
}
