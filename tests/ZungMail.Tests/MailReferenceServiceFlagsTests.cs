using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 货代行业功能开关的入库行为：关掉的能力不再抽取、不写扫描记录（这样重新开启后能补扫），
/// 但没关的能力照常工作。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MailReferenceServiceFlagsTests
{
    [Fact]
    public async Task 总开关关闭_入库不抽取也不写扫描记录()
    {
        var repository = new RecordingReferenceRepository();
        var settings = new StubSettingsStore(new AppSettings { FreightFeaturesEnabled = false });
        var service = CreateService(repository, settings);

        await service.IndexMessagesAsync(new[] { Message("SI CUT-OFF 17:00 / SO No.: SO-20261017-028") });

        Assert.Empty(repository.WrittenEntries);
        Assert.Equal(0, await service.IndexPendingAsync());
        Assert.Equal(0, repository.PendingQueries);
    }

    [Fact]
    public async Task 只关业务编号_仍识别类型且不写单号()
    {
        var repository = new RecordingReferenceRepository();
        var settings = new StubSettingsStore(new AppSettings { FreightReferencesEnabled = false });
        var service = CreateService(repository, settings);

        await service.IndexMessagesAsync(new[] { Message("SI CUT-OFF 17:00 / SO No.: SO-20261017-028") });

        var entry = Assert.Single(repository.WrittenEntries);
        Assert.Empty(entry.References);
        Assert.Equal(MailType.ShippingInstruction, entry.MailType);
    }

    [Fact]
    public async Task 只关邮件类型_类型记为Unknown但单号照常()
    {
        var repository = new RecordingReferenceRepository();
        var settings = new StubSettingsStore(new AppSettings { FreightMailTypesEnabled = false });
        var service = CreateService(repository, settings);

        await service.IndexMessagesAsync(new[] { Message("SI CUT-OFF 17:00 / SO No.: SO-20261017-028") });

        var entry = Assert.Single(repository.WrittenEntries);
        Assert.Equal(MailType.Unknown, entry.MailType);
        Assert.NotEmpty(entry.References);
    }

    [Fact]
    public async Task 全部开启_单号与类型都写入()
    {
        var repository = new RecordingReferenceRepository();
        var settings = new StubSettingsStore(new AppSettings());
        var service = CreateService(repository, settings);

        await service.IndexMessagesAsync(new[] { Message("SI CUT-OFF 17:00 / SO No.: SO-20261017-028") });

        var entry = Assert.Single(repository.WrittenEntries);
        Assert.Equal(MailType.ShippingInstruction, entry.MailType);
        Assert.NotEmpty(entry.References);
    }

    [Fact]
    public async Task 重新开启后可重置补扫进度()
    {
        var repository = new RecordingReferenceRepository();
        var service = CreateService(repository, new StubSettingsStore(new AppSettings()));

        await service.ClearScanProgressAsync();

        Assert.Equal(1, repository.ClearCalls);
    }

    private static MailReferenceService CreateService(
        IMailReferenceRepository repository,
        ISettingsStore settings)
        => new(repository, new NoopShipmentRepository(), NullLogger<MailReferenceService>.Instance, settings);

    private static EmailDetail Message(string subject) => new()
    {
        Id = 1,
        AccountId = 1,
        FolderId = 1,
        Uid = 1,
        MessageId = "<test@local>",
        Subject = subject,
        Preview = subject,
        FromEmail = "ops@forwarder.cn",
        FromName = "Ops",
        DateUtc = new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero),
        BodyState = BodyState.NotDownloaded,
        Flags = MessageFlags.None,
        Size = 1024,
    };

    private sealed class StubSettingsStore : ISettingsStore
    {
        public StubSettingsStore(AppSettings settings) => Current = settings;

        public AppSettings Current { get; }

        public string? LoadWarning => null;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>只记录写入内容的仓储替身；补扫相关调用会计数，用来断言「关闭时压根没查」。</summary>
    private sealed class RecordingReferenceRepository : IMailReferenceRepository
    {
        public List<ReferenceIndexEntry> WrittenEntries { get; } = new();

        public int PendingQueries { get; private set; }

        public int ClearCalls { get; private set; }

        public Task ReplaceRangeAsync(IReadOnlyList<ReferenceIndexEntry> entries, CancellationToken ct = default)
        {
            WrittenEntries.AddRange(entries);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PendingReferenceScan>> GetPendingScansAsync(int take = 200, CancellationToken ct = default)
        {
            PendingQueries++;
            return Task.FromResult<IReadOnlyList<PendingReferenceScan>>(Array.Empty<PendingReferenceScan>());
        }

        public Task ClearScanProgressAsync(CancellationToken ct = default)
        {
            ClearCalls++;
            return Task.CompletedTask;
        }

        public Task ReplaceForMessageAsync(long emailId, IReadOnlyList<FreightReference> references, bool bodyIndexed, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FreightReference>> GetForMessageAsync(long emailId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FreightReference>>(Array.Empty<FreightReference>());

        public Task<IReadOnlyDictionary<long, IReadOnlyList<FreightReference>>> GetForMessagesAsync(
            IReadOnlyList<long> emailIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<FreightReference>>>(
                new Dictionary<long, IReadOnlyList<FreightReference>>());

        public Task<IReadOnlyList<FreightReferenceSummary>> GetTopAsync(long? accountId = null, int take = 12, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FreightReferenceSummary>>(Array.Empty<FreightReferenceSummary>());

        public Task<int> CountPendingScansAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task InvalidateAsync(long emailId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopShipmentRepository : IShipmentRepository
    {
        public Task ApplyAsync(IReadOnlyList<ShipmentIndexEntry> entries, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DeadlineBoardItem>> GetUpcomingDeadlinesAsync(
            int take = 8, DateTimeOffset? fromUtc = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeadlineBoardItem>>(Array.Empty<DeadlineBoardItem>());

        public Task<IReadOnlyList<DeadlineBoardItem>> GetDeadlinesForMessageAsync(long emailId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeadlineBoardItem>>(Array.Empty<DeadlineBoardItem>());

        public Task<IReadOnlyList<ShipmentCaseSummary>> GetRecentCasesAsync(int take = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShipmentCaseSummary>>(Array.Empty<ShipmentCaseSummary>());

        public Task<ShipmentCaseDetail?> GetCaseAsync(long caseId, CancellationToken ct = default)
            => Task.FromResult<ShipmentCaseDetail?>(null);

        public Task<long> GetCaseIdForMessageAsync(long emailId, CancellationToken ct = default) => Task.FromResult(0L);

        public Task SetCaseStatusAsync(long caseId, ShipmentCaseStatus status, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ShipmentCaseExport?> GetCaseExportAsync(long caseId, CancellationToken ct = default)
            => Task.FromResult<ShipmentCaseExport?>(null);
    }
}
