using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Configuration;
using ZungMail.Infrastructure.Security;
using ZungMail.Infrastructure.Storage;
using ZungMail.Mail;
using MailKit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CoreFlags = ZungMail.Core.Domain.MessageFlags;

namespace ZungMail.Tests;

/// <summary>
/// IMAP 同步协议的确定性验证：用替身会话复现断线、UIDVALIDITY 变化、FETCH 降级、
/// 标志位校准窗口与正文下载失败等真实服务器难以稳定构造的场景。
/// </summary>
[Trait("Category", "Unit")]
public sealed class ImapSyncProtocolTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SqliteConnectionFactory _factory;
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly IContactRepository _contacts;
    private readonly ISettingsStore _settings;

    public ImapSyncProtocolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-protocol-" + Guid.NewGuid().ToString("N")[..8]);
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        _factory = new SqliteConnectionFactory(_paths);
        new DatabaseInitializer(_factory).Initialize();

        var protector = new AesCredentialProtector(_paths);
        _accounts = new SqliteAccountRepository(_factory, protector);
        _folders = new SqliteFolderRepository(_factory);
        _messages = new SqliteMessageRepository(_factory);
        _contacts = new SqliteContactRepository(_factory);
        _settings = new JsonSettingsStore(_paths);
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
    public async Task 同步_连接中断_重连后重试一次并完成导入()
    {
        var mailbox = new FakeMailbox { UidValidity = 7, FailOpenTimes = 1 };
        mailbox.Uids.Add(1);
        var (sync, factory, accountId, _) = await CreateSyncAsync(mailbox, uidValidity: 7, lastMaxUid: 0);

        var result = await sync.SyncAccountAsync(accountId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.NewMessages);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(1, await _messages.CountAsync(accountId));
    }

    [Fact]
    public async Task 同步_UIDVALIDITY变化_清空本地并全量重同步()
    {
        var mailbox = new FakeMailbox { UidValidity = 200 };
        mailbox.Uids.AddRange(new uint[] { 1, 2, 3 });
        var (sync, _, accountId, folderId) = await CreateSyncAsync(mailbox, uidValidity: 100, lastMaxUid: 50);

        // 旧 UID 时代的残留邮件，UIDVALIDITY 变化后必须一并清掉
        await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = accountId,
            FolderId = folderId,
            Uid = 3,
            Subject = "旧邮件",
            DateUtc = DateTimeOffset.UtcNow
        });
        Assert.Equal(1, await _messages.CountAsync(folderId: folderId));

        var result = await sync.SyncAccountAsync(accountId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.NewMessages);

        var folder = await _folders.GetAsync(folderId);
        Assert.NotNull(folder);
        Assert.Equal(200u, folder!.UidValidity);
        Assert.Equal(3u, folder.LastMaxUid);
        Assert.Equal(3, await _messages.CountAsync(folderId: folderId));
    }

    [Fact]
    public async Task 同步_FETCH格式异常_降级字段后重试()
    {
        var mailbox = new FakeMailbox { UidValidity = 7, FailFetchTimes = 1 };
        mailbox.Uids.Add(1);
        var (sync, factory, accountId, _) = await CreateSyncAsync(mailbox, uidValidity: 7, lastMaxUid: 0);

        var result = await sync.SyncAccountAsync(accountId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.NewMessages);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(2, mailbox.RequestedItems.Count);
        Assert.NotEqual(0, (int)(mailbox.RequestedItems[0] & MessageSummaryItems.PreviewText));
        Assert.Equal(0, (int)(mailbox.RequestedItems[1] & MessageSummaryItems.PreviewText));
    }

    [Fact]
    public async Task 同步_标志位校准只读取末尾窗口()
    {
        const int total = 350;
        var mailbox = new FakeMailbox { UidValidity = 7 };
        for (uint uid = 1; uid <= total; uid++) mailbox.Uids.Add(uid);
        mailbox.Flags[100] = MailKit.MessageFlags.Flagged;

        var (sync, _, accountId, folderId) = await CreateSyncAsync(mailbox, uidValidity: 7, lastMaxUid: total);

        var outside = await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = accountId, FolderId = folderId, Uid = 10, Subject = "窗口外", DateUtc = DateTimeOffset.UtcNow
        });
        var inside = await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = accountId, FolderId = folderId, Uid = 100, Subject = "窗口内", DateUtc = DateTimeOffset.UtcNow
        });

        var result = await sync.SyncAccountAsync(accountId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.NewMessages);
        Assert.Equal(1, result.UpdatedFlags);

        // 350 - 300 = 50：只从第 50 个序位开始读，绝不触达更早的历史邮件
        Assert.NotEmpty(mailbox.IndexWindows);
        Assert.All(mailbox.IndexWindows, window => Assert.True(window.Start >= 50, $"窗口起点 {window.Start}"));

        Assert.Equal(CoreFlags.None, (await _messages.GetDetailAsync(outside))!.Flags);
        Assert.True(((await _messages.GetDetailAsync(inside))!.Flags & CoreFlags.Flagged) != 0);
    }

    [Fact]
    public async Task 下载正文_首次失败可重试且不清除正文状态()
    {
        var mailbox = new FakeMailbox { UidValidity = 7 };
        mailbox.Uids.Add(1);
        mailbox.MessageFactory = call => call == 1
            ? throw new IOException("模拟连接中断")
            : FakeMailbox.BuildMessage("第二次成功");

        var (sync, _, accountId, folderId) = await CreateSyncAsync(mailbox, uidValidity: 7, lastMaxUid: 1);
        var id = await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = accountId,
            FolderId = folderId,
            Uid = 1,
            Subject = "正文重试",
            DateUtc = DateTimeOffset.UtcNow
        });

        Assert.False(await sync.DownloadBodyAsync(id));
        Assert.Equal(BodyState.NotDownloaded, (await _messages.GetDetailAsync(id))!.BodyState);

        Assert.True(await sync.DownloadBodyAsync(id));

        var detail = await _messages.GetDetailAsync(id);
        Assert.NotNull(detail);
        Assert.Equal(BodyState.Downloaded, detail!.BodyState);
        Assert.Contains("第二次成功", detail.BodyText);
    }

    [Fact]
    public async Task 正文已下载后再次请求不再重复拉取全文()
    {
        var mailbox = new FakeMailbox { UidValidity = 7 };
        mailbox.Uids.Add(1);

        var (sync, _, accountId, folderId) = await CreateSyncAsync(mailbox, uidValidity: 7, lastMaxUid: 1);
        var id = await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = accountId,
            FolderId = folderId,
            Uid = 1,
            Subject = "重复下载",
            DateUtc = DateTimeOffset.UtcNow
        });

        Assert.True(await sync.DownloadBodyAsync(id));
        var calls = mailbox.MessageCalls;
        Assert.Equal(1, calls);

        // 二次请求：正文已在库里，直接返回，不再建连接、不再拉全文
        Assert.True(await sync.DownloadBodyAsync(id));
        Assert.Equal(calls, mailbox.MessageCalls);
    }

    [Fact]
    public async Task 正文下载被取消时不建立连接()
    {
        var mailbox = new FakeMailbox { UidValidity = 7 };
        mailbox.Uids.Add(1);

        var (sync, factory, accountId, folderId) = await CreateSyncAsync(mailbox, uidValidity: 7, lastMaxUid: 1);
        var id = await _messages.UpsertAsync(new EmailDetail
        {
            AccountId = accountId,
            FolderId = folderId,
            Uid = 1,
            Subject = "取消下载",
            DateUtc = DateTimeOffset.UtcNow
        });

        // 用户在排队阶段就切走了（快速连点）：不该再开连接
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.DownloadBodyAsync(id, cts.Token));

        Assert.Equal(0, factory.ConnectCount);
        Assert.Equal(0, mailbox.MessageCalls);
    }

    [Fact]
    public async Task 同步_正文预取窗口_只下载窗口内的正文与附件()
    {
        var mailbox = new FakeMailbox { UidValidity = 7 };
        mailbox.Uids.AddRange(new uint[] { 1, 2 });
        mailbox.Dates[1] = DateTimeOffset.UtcNow.AddDays(-10);  // 窗口外：只留邮件头
        mailbox.Dates[2] = DateTimeOffset.UtcNow.AddHours(-2);  // 窗口内：同步时就下正文

        var (sync, _, accountId, folderId) = await CreateSyncAsync(mailbox, uidValidity: 7, lastMaxUid: 0);
        _settings.Current.BodyPrefetchDays = 7;

        var result = await sync.SyncAccountAsync(accountId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.NewMessages);

        var inside = await _messages.FindByUidAsync(folderId, 2);
        Assert.Equal(BodyState.Downloaded, inside!.BodyState);
        Assert.Contains("hello body", inside.BodyText);

        var outside = await _messages.FindByUidAsync(folderId, 1);
        Assert.Equal(BodyState.NotDownloaded, outside!.BodyState);
        Assert.Equal(string.Empty, outside.BodyText);

        // 窗口外的邮件在选中时按需下载，不影响已预取的那封
        Assert.True(await sync.DownloadBodyAsync(outside.Id));
        Assert.Equal(BodyState.Downloaded, (await _messages.GetDetailAsync(outside.Id))!.BodyState);
    }

    [Fact]
    public async Task 同步_关闭正文预取_新邮件只留邮件头()
    {
        var mailbox = new FakeMailbox { UidValidity = 7 };
        mailbox.Uids.Add(1);
        var (sync, _, accountId, folderId) = await CreateSyncAsync(mailbox, uidValidity: 7, lastMaxUid: 0);
        _settings.Current.BodyPrefetchDays = 0;

        var result = await sync.SyncAccountAsync(accountId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.NewMessages);
        Assert.Equal(0, mailbox.MessageCalls);

        var detail = await _messages.FindByUidAsync(folderId, 1);
        Assert.Equal(BodyState.NotDownloaded, detail!.BodyState);
    }

    [Fact]
    public async Task 同步_正文预取失败_不影响邮件头同步()
    {
        var mailbox = new FakeMailbox { UidValidity = 7 };
        mailbox.Uids.Add(1);
        mailbox.MessageFactory = _ => throw new IOException("模拟正文下载中断");
        var (sync, _, accountId, folderId) = await CreateSyncAsync(mailbox, uidValidity: 7, lastMaxUid: 0);
        _settings.Current.BodyPrefetchDays = 7;

        var result = await sync.SyncAccountAsync(accountId);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.NewMessages);
        Assert.Equal(BodyState.NotDownloaded, (await _messages.FindByUidAsync(folderId, 1))!.BodyState);
    }

    [Fact]
    public async Task 批量移动_一条连接一条命令带走整批()
    {
        var mailbox = new FakeMailbox { UidValidity = 9 };
        mailbox.Uids.AddRange(new uint[] { 1, 2, 3 });
        mailbox.KnownFolders.Add("Archive");
        var (sync, factory, accountId, folderId) = await CreateSyncAsync(mailbox, uidValidity: 9, lastMaxUid: 0);
        await sync.SyncAccountAsync(accountId);

        var ids = (await _messages.QueryAsync(new MessageQuery { FolderId = folderId, Take = 10 }))
            .Items.Select(item => item.Id).ToList();
        Assert.Equal(3, ids.Count);

        var before = factory.ConnectCount;
        var results = await sync.MoveToFolderBatchAsync(ids, accountId, "Archive");

        Assert.All(ids, id => Assert.Equal(MovePushResult.Moved, results[id]));
        Assert.Equal(before + 1, factory.ConnectCount);      // 整批只多建一条连接
        var move = Assert.Single(mailbox.BatchMoves);        // 也只有一条 MOVE 命令
        Assert.Equal(new uint[] { 1, 2, 3 }, move.OrderBy(uid => uid).ToArray());
    }

    [Fact]
    public async Task 同步顺序_主账户排在最前()
    {
        var mailbox = new FakeMailbox { UidValidity = 9 };
        mailbox.Uids.Add(1);
        var (sync, _, primaryId, _) = await CreateSyncAsync(mailbox, uidValidity: 9, lastMaxUid: 0);
        Assert.True((await _accounts.GetAsync(primaryId))!.IsPrimary);

        // 之后新增的账户都是副账户
        var secondaryId = await _accounts.AddAsync(new Account
        {
            Name = "副账户",
            Email = "other@example.com",
            Username = "other@example.com",
            Secret = "secret",
            AuthKind = AuthKind.AuthCode,
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com",
            IsEnabled = true
        });
        Assert.False((await _accounts.GetAsync(secondaryId))!.IsPrimary);
        await _folders.UpsertAsync(new ZungMail.Core.Domain.MailFolder
        {
            AccountId = secondaryId,
            FullName = "INBOX",
            DisplayName = "收件箱",
            Kind = FolderKind.Inbox,
            IsSelectable = true
        });

        var results = await sync.SyncAllAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal(primaryId, results[0].AccountId);
        Assert.Equal(secondaryId, results[1].AccountId);
    }

    private async Task<(ImapSyncService Sync, FakeImapClientFactory Factory, long AccountId, long FolderId)> CreateSyncAsync(
        FakeMailbox mailbox, uint uidValidity, uint lastMaxUid)
    {
        await _settings.LoadAsync();

        // 默认关闭正文预取：既有用例只验证邮件头与标志位，
        // 需要正文的用例显式设置窗口后再同步（见「正文预取窗口」用例）。
        _settings.Current.BodyPrefetchDays = 0;

        var accountId = await _accounts.AddAsync(new Account
        {
            Name = "协议测试账户",
            Email = "me@example.com",
            DisplayName = "协议测试账户",
            Username = "me@example.com",
            Secret = "secret",
            AuthKind = AuthKind.AuthCode,
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com",
            IsEnabled = true
        });

        var folderId = await _folders.UpsertAsync(new ZungMail.Core.Domain.MailFolder
        {
            AccountId = accountId,
            FullName = mailbox.FullName,
            DisplayName = mailbox.Name,
            Kind = FolderKind.Inbox,
            IsSelectable = true
        });
        await _folders.UpdateSyncStateAsync(folderId, uidValidity, lastMaxUid, DateTimeOffset.UtcNow);

        var factory = new FakeImapClientFactory(mailbox);
        var sync = new ImapSyncService(
            _accounts, _folders, _messages, _contacts, _settings, _paths,
            NullLogger<ImapSyncService>.Instance, factory);

        return (sync, factory, accountId, folderId);
    }
}
