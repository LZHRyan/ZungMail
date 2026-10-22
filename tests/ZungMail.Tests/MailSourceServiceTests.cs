using System.Text;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Security;
using ZungMail.Infrastructure.Storage;
using ZungMail.Mail;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 查看邮件源码：优先取服务器原始 MIME（BODY[]），失败回退按本地库字段重建；
/// 顺带覆盖编码探测、超长截断与「完全取不到」的失败边界。
/// </summary>
[Trait("Category", "Integration")]
public sealed class MailSourceServiceTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;
    private readonly SqliteAccountRepository _accounts;
    private readonly SqliteFolderRepository _folders;
    private readonly FakeMailbox _mailbox = new();
    private readonly FakeImapClientFactory _imap;
    private readonly StubSyncService _sync;
    private readonly MailSourceService _service;

    public MailSourceServiceTests()
    {
        MailCharsets.EnsureRegistered();

        _root = Path.Combine(Path.GetTempPath(), "zungmail-source-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();

        _messages = new SqliteMessageRepository(_factory);
        _accounts = new SqliteAccountRepository(_factory, new DpapiCredentialProtector(paths));
        _folders = new SqliteFolderRepository(_factory);
        _imap = new FakeImapClientFactory(_mailbox);
        _sync = new StubSyncService(_messages);
        _service = new MailSourceService(
            _messages, _accounts, _folders, _sync, _imap, NullLogger<MailSourceService>.Instance);

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

    /// <summary>入库一封邮件；<paramref name="withBody"/> = true 时连正文一起写入（模拟已打开过）。</summary>
    private async Task<long> AddMessageAsync(string subject, uint uid, bool withBody)
    {
        var id = await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = 1,
            FolderId = 1,
            Uid = uid,
            Subject = subject,
            FromName = "发货人",
            FromEmail = "sender@example.com",
            To = new List<EmailAddress> { new() { Address = "me@example.com" } },
            DateUtc = new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.Zero),
            MessageId = $"<{uid}@example.com>",
            Size = 2048,
            BodyState = BodyState.NotDownloaded
        });

        if (withBody)
        {
            await _messages.SaveBodyAsync(id, "<p>正文内容</p>", "正文内容", Array.Empty<AttachmentInfo>());
        }

        _mailbox.Uids.Add(uid);
        return id;
    }

    private static byte[] Utf8Source(string subject, string bodyText) =>
        Encoding.UTF8.GetBytes(
            "From: =?utf-8?B?5Y+R6LSn5Lq6?= <sender@example.com>\r\n" +
            "To: me@example.com\r\n" +
            $"Subject: =?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(subject))}?=\r\n" +
            "Received: from mail.example.com by mx.example.com\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            bodyText + "\r\n");

    [Fact]
    public async Task 服务器原始源码_原样返回并标明来源()
    {
        var id = await AddMessageAsync("订舱确认", uid: 42, withBody: true);
        var raw = Utf8Source("订舱确认", "原始正文 SO-12345");
        _mailbox.RawMessages[42] = raw;

        var snapshot = await _service.LoadAsync(id);

        Assert.Equal(MailSourceKind.ServerRaw, snapshot.Kind);
        Assert.Equal("服务器原始源码", snapshot.KindLabel);
        Assert.Equal("UTF-8", snapshot.EncodingName);
        Assert.Null(snapshot.FallbackReason);
        Assert.False(snapshot.IsTruncated);
        // 字节必须与服务器返回逐字节一致（另存 .eml 的价值就在这里）
        Assert.Equal(raw, snapshot.Bytes);
        Assert.Contains("Received: from mail.example.com", snapshot.Text);
        Assert.Contains("原始正文 SO-12345", snapshot.Text);
        Assert.Equal(42u, Assert.Single(_mailbox.RawRequests));
    }

    [Fact]
    public async Task 服务器取不到_回退本地重建并说明原因()
    {
        var id = await AddMessageAsync("Arrival Notice", uid: 7, withBody: true);
        _mailbox.FailRawTimes = 1;

        var snapshot = await _service.LoadAsync(id);

        Assert.Equal(MailSourceKind.Rebuilt, snapshot.Kind);
        Assert.Equal("本地重建源码", snapshot.KindLabel);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.FallbackReason));
        Assert.Contains("服务器拒绝了读取请求", snapshot.FallbackReason);
        // 重建的是可被其它客户端打开的邮件：头部 + 正文都在
        // （非 ASCII 主题在 RFC 2047 里会变成 encoded-word，这里用 ASCII 主题便于直接断言）
        Assert.Contains("Subject: Arrival Notice", snapshot.Text);
        Assert.Contains("正文内容", snapshot.Text);
        Assert.True(snapshot.Bytes.Length > 0);
    }

    [Fact]
    public async Task 正文未下载_先补下载再给出重建源码()
    {
        var id = await AddMessageAsync("费用确认", uid: 9, withBody: false);
        _mailbox.FailRawTimes = 1;

        var snapshot = await _service.LoadAsync(id);

        Assert.Equal(1, _sync.DownloadCalls);
        Assert.Equal(MailSourceKind.Rebuilt, snapshot.Kind);
        Assert.Contains("正文内容", snapshot.Text);
    }

    [Fact]
    public async Task 服务器失败且本地无正文_给出可重试的失败原因()
    {
        var id = await AddMessageAsync("提箱单", uid: 11, withBody: false);
        _mailbox.FailRawTimes = 1;
        _sync.DownloadSucceeds = false;

        var ex = await Assert.ThrowsAsync<MailSourceUnavailableException>(() => _service.LoadAsync(id));

        Assert.Contains("无法获取源码", ex.Message);
        Assert.Contains("正文下载失败", ex.Message);
        Assert.Equal(1, _sync.DownloadCalls);
    }

    [Fact]
    public async Task 旧式中文编码_按GB18030解码()
    {
        var id = await AddMessageAsync("报关资料", uid: 21, withBody: true);

        // GBK 的「主题」与正文：严格 UTF-8 会解码失败，必须回退到 GB18030
        var gbk = Encoding.GetEncoding("GB18030");
        var raw = gbk.GetBytes(
            "From: sender@example.com\r\n" +
            "Subject: 报关资料\r\n" +
            "Content-Type: text/plain; charset=gb2312\r\n" +
            "\r\n" +
            "请查收报关资料\r\n");
        _mailbox.RawMessages[21] = raw;

        var snapshot = await _service.LoadAsync(id);

        Assert.Equal(MailSourceKind.ServerRaw, snapshot.Kind);
        Assert.Equal("GB18030", snapshot.EncodingName);
        Assert.Contains("报关资料", snapshot.Text);
        Assert.Contains("请查收报关资料", snapshot.Text);
    }

    [Fact]
    public async Task 超大源码_显示截断但字节完整()
    {
        var id = await AddMessageAsync("带大附件的邮件", uid: 31, withBody: true);

        var head = Encoding.ASCII.GetBytes("Subject: big\r\n\r\n");
        var payload = Encoding.ASCII.GetBytes(new string('A', 5 * 1024 * 1024));
        var raw = head.Concat(payload).ToArray();
        _mailbox.RawMessages[31] = raw;

        var snapshot = await _service.LoadAsync(id);

        Assert.True(snapshot.IsTruncated);
        Assert.Contains("已截断显示", snapshot.Text);
        // 显示被裁剪，另存用的字节仍完整
        Assert.True(snapshot.Text.Length < raw.Length);
        Assert.Equal(raw.Length, snapshot.Bytes.Length);
    }

    [Fact]
    public async Task 关闭服务器取回_直接使用本地重建()
    {
        var id = await AddMessageAsync("草稿回看", uid: 5, withBody: true);

        var snapshot = await _service.LoadAsync(id, preferServerSource: false);

        Assert.Equal(MailSourceKind.Rebuilt, snapshot.Kind);
        Assert.Empty(_mailbox.RawRequests);
    }

    [Fact]
    public async Task 查看源码_不改变已读与星标()
    {
        var id = await AddMessageAsync("只读回看", uid: 77, withBody: true);
        _mailbox.RawMessages[77] = Utf8Source("Read only", "body");

        var before = await _messages.GetDetailAsync(id);
        Assert.Equal(MessageFlags.None, before!.Flags);

        await _service.LoadAsync(id);

        var after = await _messages.GetDetailAsync(id);
        // 查看源码是纯只读操作：既不标已读，也不写库
        Assert.Equal(MessageFlags.None, after!.Flags);
        Assert.Equal(before.BodyState, after.BodyState);
    }

    /// <summary>只实现「补下正文」，其余同步入口在查看源码场景不会被调用。</summary>
    private sealed class StubSyncService : IMailSyncService
    {
        private readonly IMessageRepository _messages;

        public StubSyncService(IMessageRepository messages) => _messages = messages;

        public int DownloadCalls { get; private set; }

        public bool DownloadSucceeds { get; set; } = true;

        public event EventHandler<SyncProgress>? ProgressChanged
        {
            add { }
            remove { }
        }

        public async Task<bool> DownloadBodyAsync(long emailId, CancellationToken ct = default)
        {
            DownloadCalls++;
            if (!DownloadSucceeds) return false;

            await _messages.SaveBodyAsync(emailId, "<p>正文内容</p>", "正文内容", Array.Empty<AttachmentInfo>(), ct);
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
