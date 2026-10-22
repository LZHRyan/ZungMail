using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using ZungMail.Infrastructure.Configuration;
using ZungMail.Infrastructure.Security;
using ZungMail.Infrastructure.Storage;
using ZungMail.Mail;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 草稿箱写入：保存草稿要在「草稿箱」文件夹里落一封草稿邮件（本地先落库，再尽力 APPEND 到服务器），
/// 再次保存覆盖同一封，发送成功后删除。这是「保存了草稿却在草稿箱找不到」的回归测试。
/// </summary>
[Trait("Category", "Integration")]
public sealed class MailDraftServiceTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMessageRepository _messages;
    private readonly SqliteFolderRepository _folders;
    private readonly SqliteAccountRepository _accounts;
    private readonly FakeMailbox _mailbox = new() { FullName = "Drafts", Name = "草稿箱" };
    private readonly MailDraftService _service;

    public MailDraftServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-draft-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        _factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(_factory).Initialize();

        _messages = new SqliteMessageRepository(_factory);
        _folders = new SqliteFolderRepository(_factory);
        _accounts = new SqliteAccountRepository(_factory, new AesCredentialProtector(paths));

        var settings = new JsonSettingsStore(paths);
        var imap = new ImapSyncService(
            _accounts,
            _folders,
            _messages,
            new SqliteContactRepository(_factory),
            settings,
            paths,
            NullLogger<ImapSyncService>.Instance,
            new FakeImapClientFactory(_mailbox));

        _service = new MailDraftService(_accounts, _folders, _messages, imap, NullLogger<MailDraftService>.Instance);

        SeedAccountAndFolders();
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
    public async Task 保存草稿_草稿箱里出现一封草稿邮件()
    {
        var id = await _service.SaveAsync(Payload());

        Assert.NotNull(id);
        var detail = await _messages.GetDetailAsync(id!.Value);
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.FolderId);                       // 2 = 草稿箱
        Assert.True((detail.Flags & MessageFlags.Draft) != 0);
        Assert.Equal("订舱确认 SO123", detail.Subject);
        Assert.Contains("正文内容", detail.BodyText);
        Assert.Contains("buyer@forwarder.cn", detail.To[0].Address);
        Assert.True(detail.BodyState == BodyState.Downloaded);
    }

    [Fact]
    public async Task 保存草稿_服务端也收到带Draft标志的副本()
    {
        await _service.SaveAsync(Payload());

        var appended = Assert.Single(_mailbox.Appended);
        Assert.True((appended.Flags & MailKit.MessageFlags.Draft) != 0);
        Assert.Equal("订舱确认 SO123", appended.Message.Subject);
    }

    [Fact]
    public async Task 再次保存_草稿箱只有一封()
    {
        var first = await _service.SaveAsync(Payload());
        var second = await _service.SaveAsync(Payload("订舱确认 SO123（改）"), first);

        Assert.NotNull(second);
        Assert.Equal(1, await CountDraftsAsync());
        var detail = await _messages.GetDetailAsync(second!.Value);
        Assert.Equal("订舱确认 SO123（改）", detail!.Subject);
    }

    [Fact]
    public async Task 删除草稿_草稿箱里不再有它()
    {
        var id = await _service.SaveAsync(Payload());

        await _service.DeleteAsync(id!.Value);

        Assert.Equal(0, await CountDraftsAsync());
        Assert.Null(await _messages.GetDetailAsync(id.Value));
    }

    [Fact]
    public async Task 没有草稿箱文件夹_返回null且不报错()
    {
        using (var connection = _factory.Create())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM folders WHERE id = 2;";
            command.ExecuteNonQuery();
        }

        var id = await _service.SaveAsync(Payload());

        Assert.Null(id);
    }

    private static DraftMailPayload Payload(string subject = "订舱确认 SO123")
        => new(1, "buyer@forwarder.cn", "", "", subject, "<p>正文内容</p>", Array.Empty<string>());

    private async Task<int> CountDraftsAsync()
        => await _messages.CountAsync(accountId: 1, folderId: 2);

    private void SeedAccountAndFolders()
    {
        using var connection = _factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO accounts(id, name, email, created_utc) VALUES (1, '测试账户', 'me@example.com', '2026-01-01T00:00:00.0000000+00:00');
INSERT INTO folders(id, account_id, full_name, display_name, kind) VALUES (1, 1, 'INBOX', '收件箱', 1);
INSERT INTO folders(id, account_id, full_name, display_name, kind) VALUES (2, 1, 'Drafts', '草稿箱', 3);
""";
        command.ExecuteNonQuery();
    }
}
