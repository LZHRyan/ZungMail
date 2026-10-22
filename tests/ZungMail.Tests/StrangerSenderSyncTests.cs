using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Configuration;
using ZungMail.Infrastructure.Security;
using ZungMail.Infrastructure.Storage;
using ZungMail.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 陌生发件人判定接在真实同步链路里的行为：判定发生在写库之前、同批多封一起标、
/// 第二轮不再标、通讯录与自有账户地址视为熟人。
/// </summary>
[Trait("Category", "Integration")]
public sealed class StrangerSenderSyncTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteAccountRepository _accounts;
    private readonly SqliteFolderRepository _folders;
    private readonly SqliteMessageRepository _messages;
    private readonly SqliteContactRepository _contacts;
    private readonly JsonSettingsStore _settings;
    private readonly FakeMailbox _mailbox = new() { UidValidity = 21 };

    public StrangerSenderSyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-stranger-sync-" + Guid.NewGuid().ToString("N")[..8]);
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
        SqliteConnectionFactoryRelease();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响验证结论
        }
    }

    private void SqliteConnectionFactoryRelease() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private async Task<long> CreateAccountAsync()
    {
        await _settings.LoadAsync();
        await _accounts.AddAsync(new Account
        {
            Name = "工作邮箱",
            Email = "me@example.com",
            Username = "me@example.com",
            Secret = "auth-code",
            ImapHost = "imap.example.com",
            ImapPort = 993,
            SmtpHost = "smtp.example.com",
            SmtpPort = 465
        });

        var all = await _accounts.GetAllAsync();
        return all.Single().Id;
    }

    private ImapSyncService CreateSync() => new(
        _accounts,
        _folders,
        _messages,
        _contacts,
        _settings,
        _paths,
        NullLogger<ImapSyncService>.Instance,
        new FakeImapClientFactory(_mailbox));

    private bool ReadStrangerFlag(uint uid)
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(is_stranger), 0) FROM messages WHERE uid = @uid;";
        command.Parameters.AddWithValue("@uid", (long)uid);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L) > 0L;
    }

    [Fact]
    public async Task 首轮同发件人三封全部标记为陌生()
    {
        var accountId = await CreateAccountAsync();
        _mailbox.Uids.AddRange(new uint[] { 1, 2, 3 });
        foreach (var uid in new uint[] { 1, 2, 3 }) _mailbox.Senders[uid] = "newagent@partner-mail.com";

        await CreateSync().SyncAccountAsync(accountId);

        Assert.True(ReadStrangerFlag(1));
        Assert.True(ReadStrangerFlag(2));
        Assert.True(ReadStrangerFlag(3));
    }

    [Fact]
    public async Task 第二轮同发件人不再标记_且旧标识保留()
    {
        var accountId = await CreateAccountAsync();
        _mailbox.Uids.AddRange(new uint[] { 1, 2 });
        _mailbox.Senders[1] = "newagent@partner-mail.com";
        _mailbox.Senders[2] = "newagent@partner-mail.com";

        var sync = CreateSync();
        await sync.SyncAccountAsync(accountId);
        Assert.True(ReadStrangerFlag(1));
        Assert.True(ReadStrangerFlag(2));

        // 第二轮：同一个人又发来一封
        _mailbox.Uids.Add(3);
        _mailbox.Senders[3] = "newagent@partner-mail.com";
        await sync.SyncAccountAsync(accountId);

        Assert.False(ReadStrangerFlag(3));
        Assert.True(ReadStrangerFlag(1));
        Assert.True(ReadStrangerFlag(2));
    }

    [Fact]
    public async Task 通讯录里已有该地址时不标记()
    {
        var accountId = await CreateAccountAsync();
        await _contacts.UpsertManyAsync(new[] { new Contact { Email = "known@partner.com", Name = "老客户" } });
        _mailbox.Uids.Add(1);
        _mailbox.Senders[1] = "known@partner.com";

        await CreateSync().SyncAccountAsync(accountId);

        Assert.False(ReadStrangerFlag(1));
    }

    [Fact]
    public async Task 账户自身地址不标记()
    {
        var accountId = await CreateAccountAsync();
        _mailbox.Uids.Add(1);
        _mailbox.Senders[1] = "ME@Example.com"; // 大小写不同也要命中自有地址

        await CreateSync().SyncAccountAsync(accountId);

        Assert.False(ReadStrangerFlag(1));
    }

    [Fact]
    public async Task 同企业域名的新地址不算陌生()
    {
        var accountId = await CreateAccountAsync();

        // 账户是 me@example.com，企业域 example.com：同事用新地址第一次来信不算陌生人
        _mailbox.Uids.Add(1);
        _mailbox.Senders[1] = "newcolleague@example.com";

        await CreateSync().SyncAccountAsync(accountId);

        Assert.False(ReadStrangerFlag(1));
    }

    [Fact]
    public async Task 两个发件人同批_只有陌生那个被标记()
    {
        var accountId = await CreateAccountAsync();
        await _contacts.UpsertManyAsync(new[] { new Contact { Email = "known@partner.com" } });

        _mailbox.Uids.AddRange(new uint[] { 1, 2 });
        _mailbox.Senders[1] = "known@partner.com";
        _mailbox.Senders[2] = "stranger@unknown-shipper.cn";

        await CreateSync().SyncAccountAsync(accountId);

        Assert.False(ReadStrangerFlag(1));
        Assert.True(ReadStrangerFlag(2));
    }
}

