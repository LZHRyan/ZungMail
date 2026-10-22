using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 阅读窗格的两段式加载：邮件头先上屏（纯本地查询、不碰网络），正文再按需下载。
/// 这里用假的同步服务验证「读头不触发下载」与「下载后拿到正文」两条契约。
/// </summary>
[Trait("Category", "Integration")]
public sealed class MessageDetailLoaderTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteMessageRepository _messages;
    private readonly FakeSyncService _sync;
    private readonly MessageDetailLoader _loader;

    public MessageDetailLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-detail-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();

        _messages = new SqliteMessageRepository(_factory);
        _sync = new FakeSyncService(_messages);
        var references = new MailReferenceService(
            new SqliteMailReferenceRepository(_factory),
            new SqliteShipmentRepository(_factory),
            NullLogger<MailReferenceService>.Instance);
        _loader = new MessageDetailLoader(_messages, _sync, references);

        SeedAccountAndFolder();
    }

    private readonly SqliteConnectionFactory _factory;

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

    /// <summary>按「仅同步邮件头」的样子入库：只有主题与发件人，正文没下载。</summary>
    private Task<long> AddHeaderOnlyMessageAsync(string subject)
        => _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = 10,
            MessageId = "<header-only@example>",
            ThreadId = "<header-only@example>",
            Subject = subject,
            FromName = "Carrier Ops",
            FromEmail = "ops@carrier.example",
            DateUtc = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero),
            Preview = "SO No.: SHA1234567",
            BodyState = BodyState.NotDownloaded
        });

    [Fact]
    public async Task 只读邮件头_不触发正文下载()
    {
        var id = await AddHeaderOnlyMessageAsync("Booking Confirmation");
        var version = _loader.Begin();

        var header = await _loader.LoadHeaderAsync(id, version);

        Assert.NotNull(header);
        Assert.Equal("Booking Confirmation", header!.Subject);
        Assert.Equal(BodyState.NotDownloaded, header.BodyState);
        Assert.Equal(0, _sync.DownloadCalls); // 关键：读头不碰网络

        // 邮件头阶段就能给出的展示信息
        Assert.Equal("Carrier Ops", header.FromDisplay);
        Assert.Empty(header.BodyText);
    }

    [Fact]
    public async Task 下载正文后_返回带正文的详情()
    {
        var id = await AddHeaderOnlyMessageAsync("SI CUT-OFF");
        var version = _loader.Begin();

        var header = await _loader.LoadHeaderAsync(id, version);
        var statuses = new List<string>();

        var full = await _loader.LoadBodyAsync(header!, version, text => statuses.Add(text));

        Assert.Equal(1, _sync.DownloadCalls);
        Assert.NotNull(full);
        Assert.NotSame(header, full);
        Assert.Contains("HBL NO.: HBL55667788", full!.BodyText);
        Assert.Contains("正在下载正文…", statuses);
        Assert.Contains("就绪", statuses);
    }

    [Fact]
    public async Task 正文已下载_直接返回不重复请求()
    {
        var id = await AddHeaderOnlyMessageAsync("Arrival Notice");
        var version = _loader.Begin();
        var header = await _loader.LoadHeaderAsync(id, version);

        await _loader.LoadBodyAsync(header!, version, null);
        Assert.Equal(1, _sync.DownloadCalls);

        // 第二次用刷新后的详情（正文已落地）
        var refreshed = await _loader.LoadHeaderAsync(id, _loader.Begin());
        var again = await _loader.LoadBodyAsync(refreshed!, _loader.Begin(), null);
        Assert.Equal(1, _sync.DownloadCalls);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task 过期版本_丢弃加载结果()
    {
        var id = await AddHeaderOnlyMessageAsync("Debit Note");
        var oldVersion = _loader.Begin();
        _loader.Begin(); // 用户已经切到另一封：旧请求作废

        Assert.Null(await _loader.LoadHeaderAsync(id, oldVersion));
    }

    [Fact]
    public async Task 正文为空但已标记下载_普通加载跳过_手动重新获取才补回来()
    {
        var id = await AddHeaderOnlyMessageAsync("空正文的到港通知");

        // 复现线上现象：状态是「已下载」，但正文是空的（上次解析失败 / 缓存被清 / 服务器返回空体）
        await _messages.SaveBodyAsync(id, "", "", Array.Empty<AttachmentInfo>());

        var version = _loader.Begin();
        var header = await _loader.LoadHeaderAsync(id, version);
        Assert.NotNull(header);
        Assert.Equal(BodyState.Downloaded, header!.BodyState);
        Assert.True(string.IsNullOrWhiteSpace(header.BodyText));

        // 普通按需加载按状态判断直接跳过 —— 这正是正文永远补不回来的原因
        Assert.Same(header, await _loader.LoadBodyAsync(header, version, null));
        Assert.Equal(0, _sync.DownloadCalls);
        Assert.Equal(0, _sync.RedownloadCalls);

        // 用户点「重新获取正文」：忽略已下载标记，向服务器再要一次
        var reloaded = await _loader.ReloadBodyAsync(header, _loader.Begin(), null);

        Assert.Equal(1, _sync.RedownloadCalls);
        Assert.NotNull(reloaded);
        Assert.Contains("HBL NO.: HBL55667788", reloaded!.BodyText);
    }

    [Fact]
    public async Task 重新获取正文失败_保留原邮件头不让阅读区退回空态()
    {
        var id = await AddHeaderOnlyMessageAsync("取不到正文");
        var header = await _loader.LoadHeaderAsync(id, _loader.Begin());

        _sync.RedownloadSucceeds = false;
        var reloaded = await _loader.ReloadBodyAsync(header!, _loader.Begin(), null);

        Assert.Same(header, reloaded); // 邮件头还在，界面不至于整页空白
        Assert.Equal(1, _sync.RedownloadCalls);
    }

    /// <summary>假同步服务：下载正文时把正文写回本地库，模拟真实按需下载。</summary>
    private sealed class FakeSyncService : IMailSyncService
    {
        private readonly SqliteMessageRepository _messages;

        public FakeSyncService(SqliteMessageRepository messages) => _messages = messages;

        public int DownloadCalls { get; private set; }

        /// <summary>「重新获取正文」被调用了几次（用来区分普通按需下载与手动重拉）。</summary>
        public int RedownloadCalls { get; private set; }

        /// <summary>模拟重拉失败（服务器拒绝 / 网络不通）。</summary>
        public bool RedownloadSucceeds { get; set; } = true;

        public event EventHandler<SyncProgress>? ProgressChanged
        {
            add { }
            remove { }
        }

        public Task<bool> RedownloadBodyAsync(long emailId, CancellationToken ct = default)
        {
            RedownloadCalls++;
            return RedownloadSucceeds ? DownloadBodyAsync(emailId, ct) : Task.FromResult(false);
        }

        public Task<bool> DownloadBodyAsync(long emailId, bool includeAttachments, CancellationToken ct = default)
            => DownloadBodyAsync(emailId, ct);

        public async Task<bool> DownloadBodyAsync(long emailId, CancellationToken ct = default)
        {
            DownloadCalls++;
            await _messages.SaveBodyAsync(
                emailId,
                "<p>补料请查收，SI CUT-OFF: 2026-10-20 12:00。</p><p>HBL NO.: HBL55667788</p>",
                "补料请查收，SI CUT-OFF: 2026-10-20 12:00。\r\nHBL NO.: HBL55667788",
                Array.Empty<AttachmentInfo>(),
                ct);
            return true;
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
