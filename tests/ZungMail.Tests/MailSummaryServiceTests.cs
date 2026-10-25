using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Configuration;
using ZungMail.Infrastructure.Security;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 摘要编排：引擎顺序、自动降级、缓存、取正文、关闭开关。引擎全部用替身，不依赖真模型。
/// </summary>
[Trait("Category", "Integration")]
public sealed class MailSummaryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;
    private readonly JsonSettingsStore _settings;
    private readonly StubSync _sync = new();
    private readonly FakeProvider _local = new(MailSummarySettings.LocalModelProviderId, "本地模型");
    private readonly FakeProvider _rule = new(MailSummarySettings.RuleBasedProviderId, "规则抽取");
    private readonly MailSummaryService _service;

    public MailSummaryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-summary-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();

        _messages = new SqliteMessageRepository(_factory);
        _settings = new JsonSettingsStore(paths);
        _settings.LoadAsync().GetAwaiter().GetResult();
        _settings.Current.MailSummaryProvider = MailSummarySettings.AutoProviderId;
        // 默认关闭本地 AI 是新行为；这组测试验证「本地模型参与编排」，所以显式打开
        _settings.Current.LocalAiEnabled = true;

        _sync.Messages = _messages;

        _service = new MailSummaryService(
            _messages,
            _sync,
            _settings,
            new IMailSummaryProvider[] { _local, _rule },
            NullLogger<MailSummaryService>.Instance,
            localModelTimeout: TimeSpan.FromMilliseconds(200),
            ruleBasedTimeout: TimeSpan.FromMilliseconds(200));

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

    private async Task<long> AddMessageAsync(bool withBody)
    {
        var id = await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 1,
            Subject = "订舱确认 SO-2026-001",
            FromEmail = "sender@example.com",
            DateUtc = DateTimeOffset.Now,
            BodyState = BodyState.NotDownloaded
        });

        if (withBody)
        {
            await _messages.SaveBodyAsync(id, "<p>请安排订舱</p>", "请安排在 10-25 前提交 SI。", Array.Empty<AttachmentInfo>());
        }

        return id;
    }

    [Fact]
    public async Task 自动模式_本地可用时优先本地模型()
    {
        var id = await AddMessageAsync(withBody: true);

        var result = await _service.SummarizeAsync(id);

        Assert.Equal(MailSummarySettings.LocalModelProviderId, result.ProviderId);
        Assert.False(result.IsFallback);
        Assert.Equal(1, _local.Calls);
        Assert.Equal(0, _rule.Calls);
    }

    /// <summary>
    /// 引擎必须在线程池跑：本机小模型首次加载权重（0.47GB GGUF）要几百毫秒到几秒，
    /// 以前跟着调用方线程跑，界面线程就被拖住（实机日志里出现过 9.2 秒的占用）。
    /// </summary>
    [Fact]
    public async Task 摘要引擎不在调用线程上执行()
    {
        var id = await AddMessageAsync(withBody: true);
        _local.CaptureThread = true;

        await _service.SummarizeAsync(id);

        Assert.True(_local.CalledOnThreadPool, "摘要引擎应当在线程池线程执行，不能占用调用线程");
    }

    [Fact]
    public async Task 本地不可用_自动降级规则抽取并带原因()
    {
        _local.Available = false;
        _local.UnavailableReason = "摘要模型尚未下载";
        var id = await AddMessageAsync(withBody: true);

        var result = await _service.SummarizeAsync(id);

        Assert.Equal(MailSummarySettings.RuleBasedProviderId, result.ProviderId);
        Assert.True(result.IsFallback);
        Assert.Contains("尚未下载", result.FallbackReason);
    }

    [Fact]
    public async Task 指定规则抽取时不尝试本地模型()
    {
        _settings.Current.MailSummaryProvider = MailSummarySettings.RuleBasedProviderId;
        var id = await AddMessageAsync(withBody: true);

        var result = await _service.SummarizeAsync(id);

        Assert.Equal(MailSummarySettings.RuleBasedProviderId, result.ProviderId);
        Assert.Equal(0, _local.Calls);
    }

    [Fact]
    public async Task 本地推理超时_降级规则抽取()
    {
        _local.Delay = TimeSpan.FromSeconds(5);
        var id = await AddMessageAsync(withBody: true);

        var result = await _service.SummarizeAsync(id);

        Assert.Equal(MailSummarySettings.RuleBasedProviderId, result.ProviderId);
        Assert.Contains("已改用规则抽取", result.FallbackReason);
    }

    [Fact]
    public async Task 同一封邮件重复总结命中缓存()
    {
        var id = await AddMessageAsync(withBody: true);

        await _service.SummarizeAsync(id);
        var second = await _service.SummarizeAsync(id);

        Assert.Equal(1, _local.Calls);
        Assert.Equal(MailSummarySettings.LocalModelProviderId, second.ProviderId);
    }

    [Fact]
    public async Task 重新生成忽略缓存()
    {
        var id = await AddMessageAsync(withBody: true);

        await _service.SummarizeAsync(id);
        await _service.SummarizeAsync(id, force: true);

        Assert.Equal(2, _local.Calls);
    }

    [Fact]
    public async Task 正文未下载时先取正文再总结()
    {
        var id = await AddMessageAsync(withBody: false);

        var result = await _service.SummarizeAsync(id);

        Assert.True(_sync.DownloadCalls >= 1);
        Assert.Equal(MailSummarySettings.LocalModelProviderId, result.ProviderId);
    }

    [Fact]
    public async Task 没有正文且下载失败_抛出可重试异常()
    {
        _sync.DownloadSucceeds = false;
        var id = await AddMessageAsync(withBody: false);

        var ex = await Assert.ThrowsAsync<MailSummaryUnavailableException>(() => _service.SummarizeAsync(id));

        Assert.Contains("没有可总结的正文", ex.Message);
    }

    [Fact]
    public async Task 未启用本地AI_自动走规则抽取且不碰本地模型()
    {
        _settings.Current.LocalAiEnabled = false;
        var id = await AddMessageAsync(withBody: true);

        var result = await _service.SummarizeAsync(id);

        Assert.Equal(MailSummarySettings.RuleBasedProviderId, result.ProviderId);
        Assert.True(result.IsFallback);
        Assert.Contains("本地 AI 未启用", result.FallbackReason);
        Assert.Equal(0, _local.Calls);
    }

    [Fact]
    public async Task 引擎全部不可用_给出汇总原因()
    {
        _local.Available = false;
        _rule.Available = false;
        var id = await AddMessageAsync(withBody: true);

        var ex = await Assert.ThrowsAsync<MailSummaryUnavailableException>(() => _service.SummarizeAsync(id));

        Assert.Contains("没有可用的摘要引擎", ex.Message);
    }

    private sealed class FakeProvider : IMailSummaryProvider
    {
        public FakeProvider(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public bool Available { get; set; } = true;

        public string? UnavailableReason { get; set; }

        public TimeSpan Delay { get; set; }

        public int Calls { get; private set; }

        /// <summary>记录「引擎在哪个线程上跑」，用于守护「不占用调用线程」。</summary>
        public bool CaptureThread { get; set; }

        public bool CalledOnThreadPool { get; private set; }

        public Task<MailSummaryProviderStatus> GetStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new MailSummaryProviderStatus
            {
                Available = Available,
                UnavailableReason = Available ? null : UnavailableReason ?? "不可用"
            });

        public async Task<MailSummaryResult> SummarizeAsync(
            MailSummaryRequest request,
            IProgress<MailSummaryProgress>? progress = null,
            CancellationToken ct = default)
        {
            Calls++;
            if (CaptureThread) CalledOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);

            return new MailSummaryResult
            {
                MessageId = request.MessageId,
                ProviderId = Id,
                ProviderName = DisplayName,
                Bullets = new[] { "要点一" },
                Actions = new[] { "待处理一" },
                References = new[] { "单号一" },
                RawText = "raw",
                InputChars = request.InputChars
            };
        }
    }

    /// <summary>只实现「按需补正文」，其余同步入口用不到。</summary>
    private sealed class StubSync : IMailSyncService
    {
        public IMessageRepository? Messages { get; set; }

        public bool DownloadSucceeds { get; set; } = true;

        public int DownloadCalls { get; private set; }

        public event EventHandler<SyncProgress>? ProgressChanged
        {
            add { }
            remove { }
        }

        public async Task<bool> DownloadBodyAsync(long emailId, CancellationToken ct = default)
        {
            DownloadCalls++;
            if (!DownloadSucceeds || Messages is null) return false;

            await Messages.SaveBodyAsync(
                emailId, "<p>正文</p>", "请安排在 10-25 前提交 SI。", Array.Empty<AttachmentInfo>(), ct);
            return true;
        }

        public Task<bool> DownloadBodyAsync(long emailId, bool includeAttachments, CancellationToken ct = default)
            => DownloadBodyAsync(emailId, ct);

        public Task<bool> RedownloadBodyAsync(long emailId, CancellationToken ct = default)
            => DownloadBodyAsync(emailId, ct);

        public Task<SyncOutcome> SyncAccountAsync(long accountId, bool fullResync = false, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SyncOutcome>> SyncAllAsync(bool fullResync = false, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SyncOutcome>> SyncAllIncrementalAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SyncOutcome> SyncInboxAsync(long accountId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> TestConnectionAsync(Account account, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> PushFlagsAsync(long emailId, MessageFlags flags, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<int> PushFlagsBatchAsync(
            IReadOnlyList<(long EmailId, MessageFlags Flags)> updates, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<MovePushResult> MoveAsync(long emailId, FolderKind destination, CancellationToken ct = default)
            => Task.FromResult(MovePushResult.Moved);

        public Task<MovePushResult> MoveToFolderAsync(
            long emailId, long destinationAccountId, string destinationFullName, CancellationToken ct = default)
            => Task.FromResult(MovePushResult.Moved);

        public Task<IReadOnlyDictionary<long, MovePushResult>> MoveToFolderBatchAsync(
            IReadOnlyList<long> emailIds, long destinationAccountId, string destinationFullName, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<long, MovePushResult>>(
                emailIds.ToDictionary(id => id, _ => MovePushResult.Moved));
    }
}
