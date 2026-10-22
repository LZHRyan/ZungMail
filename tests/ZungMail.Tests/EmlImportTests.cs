using System.Text;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Storage;
using ZungMail.Mail;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Xunit;

namespace ZungMail.Tests;

/// <summary>.eml 导入：写入邮件头与正文、附件落盘、按 Message-ID 去重。</summary>
[Trait("Category", "Integration")]
public sealed class EmlImportTests : IDisposable
{
    private readonly string _root;

    public EmlImportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-import-" + Guid.NewGuid().ToString("N")[..8]);
    }

    [Fact]
    public async Task 导入_写入邮件头正文与附件_并对同一文件去重()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(factory).Initialize();

        var accounts = new SqliteAccountRepository(factory, new PassthroughProtector());
        var accountId = await accounts.AddAsync(new Account
        {
            Name = "测试账户",
            Email = "test@example.com",
            Username = "test@example.com",
            Secret = "secret",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com"
        });

        var folders = new SqliteFolderRepository(factory);
        var folderId = await folders.UpsertAsync(new MailFolder
        {
            AccountId = accountId,
            FullName = "INBOX",
            DisplayName = "收件箱",
            Kind = FolderKind.Inbox
        });

        var messages = new SqliteMessageRepository(factory);
        var service = new EmlImportService(
            messages,
            folders,
            paths,
            NullLogger<EmlImportService>.Instance);

        var emlPath = Path.Combine(_root, "booking.eml");
        await WriteSampleEmlAsync(emlPath);

        var result = await service.ImportAsync(accountId, folderId, new[] { emlPath });

        Assert.Equal(1, result.Imported);
        Assert.Equal(0, result.Duplicates);
        Assert.True(result.Success, string.Join("; ", result.Failures));

        var found = await messages.FindByMessageIdAsync(accountId, "abc123@example.com");
        Assert.NotNull(found);

        var detail = await messages.GetDetailAsync(found!.Id);
        Assert.NotNull(detail);
        Assert.Equal("船期通知：MSKU1234567", detail!.Subject);
        Assert.Equal("ops@shipping.example", detail.FromEmail);
        Assert.Equal("船务", detail.FromName);
        Assert.Equal(accountId, detail.AccountId);
        Assert.Equal(folderId, detail.FolderId);
        Assert.True(detail.HasBody);
        Assert.Contains("ETD", detail.BodyText);
        Assert.False(detail.IsUnread, "导入的历史邮件应默认标记为已读");
        Assert.True(detail.HasAttachment);
        Assert.Single(detail.Attachments);
        Assert.True(detail.Attachments[0].IsDownloaded, "附件应已落盘");
        Assert.EndsWith("booking.txt", detail.Attachments[0].LocalPath, StringComparison.OrdinalIgnoreCase);

        // 再次导入同一文件：按 Message-ID 去重，不产生副本。
        var again = await service.ImportAsync(accountId, folderId, new[] { emlPath });
        Assert.Equal(0, again.Imported);
        Assert.Equal(1, again.Duplicates);

        var page = await messages.QueryAsync(new MessageQuery { AccountId = accountId, FolderId = folderId });
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task 导入_目标文件夹与账户不匹配时拒绝()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var factory = new SqliteConnectionFactory(paths);
        new DatabaseInitializer(factory).Initialize();

        var accounts = new SqliteAccountRepository(factory, new PassthroughProtector());
        var accountId = await accounts.AddAsync(new Account
        {
            Name = "A", Email = "a@example.com", Username = "a@example.com",
            Secret = "s", ImapHost = "imap.example.com", SmtpHost = "smtp.example.com"
        });
        var otherId = await accounts.AddAsync(new Account
        {
            Name = "B", Email = "b@example.com", Username = "b@example.com",
            Secret = "s", ImapHost = "imap.example.com", SmtpHost = "smtp.example.com"
        });

        var folders = new SqliteFolderRepository(factory);
        var otherFolder = await folders.UpsertAsync(new MailFolder
        {
            AccountId = otherId, FullName = "INBOX", DisplayName = "收件箱", Kind = FolderKind.Inbox
        });

        var service = new EmlImportService(
            new SqliteMessageRepository(factory),
            folders,
            paths,
            NullLogger<EmlImportService>.Instance);

        var emlPath = Path.Combine(_root, "x.eml");
        await WriteSampleEmlAsync(emlPath);

        var result = await service.ImportAsync(accountId, otherFolder, new[] { emlPath });

        Assert.Equal(0, result.Imported);
        Assert.Single(result.Failures);
        Assert.Contains("不属于所选账户", result.Failures[0]);
    }

    private static async Task WriteSampleEmlAsync(string path)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("船务", "ops@shipping.example"));
        message.To.Add(new MailboxAddress("我", "test@example.com"));
        message.Subject = "船期通知：MSKU1234567";
        message.Date = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        message.MessageId = "abc123@example.com";

        var builder = new BodyBuilder
        {
            TextBody = "ETD 2026-10-05\nETA 2026-11-02\n请查收订舱确认。"
        };
        builder.Attachments.Add("booking.txt", Encoding.UTF8.GetBytes("booking-ok"), new ContentType("text", "plain"));
        message.Body = builder.ToMessageBody();

        await using var stream = File.Create(path);
        await message.WriteToAsync(stream);
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

    /// <summary>测试用凭据保护：Base64 往返，避免测试依赖 DPAPI 机器状态。</summary>
    private sealed class PassthroughProtector : ICredentialProtector
    {
        public string Protect(string plainText)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string Unprotect(string cipherText)
            => Encoding.UTF8.GetString(Convert.FromBase64String(cipherText));
    }
}
