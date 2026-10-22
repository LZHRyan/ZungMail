using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using ZungMail.Infrastructure.Configuration;
using ZungMail.Infrastructure.Security;
using ZungMail.Infrastructure.Storage;
using ZungMail.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ZungMail.Tests;

/// <summary>
/// 真实 IMAP 同步的集成验证。未提供凭据时不执行任何网络访问，直接返回，
/// 因此可以随 <c>dotnet test</c> 一起安全运行。
///
/// 环境变量（改名后仍兼容旧的 MAILCLIENT_TEST_* 写法）：
///   ZUNGMAIL_TEST_EMAIL      邮箱地址（必填）
///   ZUNGMAIL_TEST_SECRET     授权码 / 应用专用密码（必填）
///   ZUNGMAIL_TEST_IMAP_HOST  取件服务器（域名未命中内置预置时必填）
///   ZUNGMAIL_TEST_IMAP_PORT  默认 993（SSL）
///   ZUNGMAIL_TEST_SMTP_HOST  发件服务器（未填则只验证 IMAP）
///   ZUNGMAIL_TEST_SMTP_PORT  默认 465（SSL）
/// </summary>
[Trait("Category", "LiveMail")]
public sealed class ImapSyncIntegrationTests : IDisposable
{
    private const string EmailVar = "ZUNGMAIL_TEST_EMAIL";
    private const string SecretVar = "ZUNGMAIL_TEST_SECRET";
    private const string LegacyPrefix = "MAILCLIENT_TEST_";

    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly SqliteConnectionFactory _factory;
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly ISettingsStore _settings;

    public ImapSyncIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "zungmail-itest-" + Guid.NewGuid().ToString("N")[..8]);

        _paths = new AppPaths(_root);
        _paths.EnsureCreated();

        _factory = new SqliteConnectionFactory(_paths);
        new DatabaseInitializer(_factory).Initialize();

        var protector = new AesCredentialProtector(_paths);
        _accounts = new SqliteAccountRepository(_factory, protector);
        _folders = new SqliteFolderRepository(_factory);
        _messages = new SqliteMessageRepository(_factory);
        _settings = new JsonSettingsStore(_paths);
    }

    public void Dispose()
    {
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
    public async Task 真实邮箱_IMAP同步_导入邮件且二次同步为增量()
    {
        var email = ReadEnv(EmailVar);
        var secret = ReadEnv(SecretVar);

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(secret))
        {
            _output.WriteLine($"未设置 {EmailVar} / {SecretVar}，跳过真实 IMAP 验证。");
            return;
        }

        Account account;
        try
        {
            account = BuildAccount(email, secret);
        }
        catch (InvalidOperationException ex)
        {
            _output.WriteLine(ex.Message);
            return;
        }

        await _settings.LoadAsync();
        _output.WriteLine($"账户：{account.Email}");
        _output.WriteLine($"IMAP：{account.ImapHost}:{account.ImapPort}（{account.ImapSecure}）");
        _output.WriteLine($"SMTP：{account.SmtpHost}:{account.SmtpPort}（{account.SmtpSecure}）");
        _output.WriteLine($"本地库：{_paths.DatabasePath}");
        _output.WriteLine("");

        var sync = new ImapSyncService(
            _accounts, _folders, _messages, new SqliteContactRepository(_factory), _settings, _paths,
            NullLogger<ImapSyncService>.Instance);

        var phases = new List<string>();
        sync.ProgressChanged += (_, p) =>
        {
            var text = p.Text;
            if (phases.Count == 0 || phases[^1] != text) phases.Add(text);
        };

        // ---- 1. 连接与鉴权 ----
        var connected = await sync.TestConnectionAsync(account);
        _output.WriteLine($"[1] 测试连接：{(connected ? "成功" : "失败")} → {account.ConnectionStatus}");
        Assert.True(connected, $"连接失败：{account.ConnectionStatus}");

        // ---- 2. 首次同步 ----
        var accountId = await _accounts.AddAsync(account);
        var first = await sync.SyncAccountAsync(accountId);
        _output.WriteLine($"[2] 首次同步：{first.Summary}（耗时 {first.Duration.TotalSeconds:F1}s）");
        _output.WriteLine($"    阶段轨迹：{string.Join(" → ", phases)}");
        Assert.True(first.Success, $"首次同步失败：{first.Error}");
        Assert.True(first.FoldersSynced > 0, "未同步到任何可读文件夹，请确认 IMAP 服务已开启。");

        var folders = await _folders.GetByAccountAsync(accountId);
        _output.WriteLine($"    服务器文件夹共 {folders.Count} 个：");
        foreach (var folder in folders.OrderByDescending(f => f.Kind == FolderKind.Inbox).ThenBy(f => f.FullName))
        {
            var local = await _messages.CountAsync(accountId, folder.Id);
            _output.WriteLine(
                $"      · {folder.Title,-20} [{folder.Kind}] 本地 {local} 封 / 服务器 {folder.TotalCount} 封" +
                $"，未读 {folder.UnreadCount}，UIDVALIDITY={folder.UidValidity}，cursor={folder.LastMaxUid}" +
                (folder.IsSelectable ? "" : "（不可选）"));
        }

        var firstTotal = await _messages.CountAsync(accountId);
        _output.WriteLine($"    本地邮件总数：{firstTotal}");

        // ---- 3. 抽样邮件 ----
        var inbox = folders.FirstOrDefault(f => f.Kind == FolderKind.Inbox) ?? folders.FirstOrDefault(f => f.IsSelectable);
        if (inbox is not null)
        {
            var page = await _messages.QueryAsync(new MessageQuery
            {
                AccountId = accountId,
                FolderId = inbox.Id,
                Take = 5,
                ExcludeTrash = false
            });
            _output.WriteLine($"    抽样（{inbox.Title} 最新 {page.Items.Count} 封）：");
            foreach (var m in page.Items)
            {
                _output.WriteLine(
                    $"      · {m.DateUtc.ToLocalTime():yyyy-MM-dd HH:mm} | {m.FromName} <{m.FromEmail}> | {m.Subject}" +
                    $" | thread={m.ThreadId} | flags={m.Flags}");
            }

            var detail = page.Items.Count > 0 ? await _messages.GetDetailAsync(page.Items[0].Id) : null;
            if (detail is not null)
            {
                _output.WriteLine($"    首封正文状态：{detail.BodyState}，预览：{Truncate(detail.Preview, 60)}");
            }
        }

        // ---- 4. 二次同步：验证增量游标 ----
        var second = await sync.SyncAccountAsync(accountId);
        var secondTotal = await _messages.CountAsync(accountId);
        _output.WriteLine($"[3] 二次同步：{second.Summary}（耗时 {second.Duration.TotalSeconds:F1}s）");
        _output.WriteLine($"    本地邮件总数：{secondTotal}（对比首次 {firstTotal}）");

        Assert.True(second.Success, $"二次同步失败：{second.Error}");
        Assert.Equal(0, second.NewMessages);
        Assert.Equal(firstTotal, secondTotal);

        _output.WriteLine("");
        _output.WriteLine("结论：IMAP 连接、文件夹同步、邮件头导入与增量游标均验证通过。");
    }

    private static Account BuildAccount(string email, string secret)
    {
        var preset = new ServerConfigResolver().Resolve(email);

        var imapHost = preset?.ImapHost ?? ReadEnv("ZUNGMAIL_TEST_IMAP_HOST");
        if (string.IsNullOrWhiteSpace(imapHost))
        {
            throw new InvalidOperationException(
                $"域名 {email[(email.IndexOf('@') + 1)..]} 未命中内置服务器预置，" +
                "请通过 ZUNGMAIL_TEST_IMAP_HOST 指定取件服务器。");
        }

        var smtpHost = preset?.SmtpHost ?? ReadEnv("ZUNGMAIL_TEST_SMTP_HOST");

        return new Account
        {
            Name = "集成测试账户",
            Email = email,
            DisplayName = email,
            Username = email,
            Secret = secret,
            AuthKind = AuthKind.AuthCode,
            ImapHost = imapHost,
            ImapPort = preset?.ImapPort ?? ReadPort("ZUNGMAIL_TEST_IMAP_PORT", 993),
            ImapSecure = preset?.ImapSecure ?? SecureMode.Ssl,
            SmtpHost = smtpHost ?? string.Empty,
            SmtpPort = preset?.SmtpPort ?? ReadPort("ZUNGMAIL_TEST_SMTP_PORT", 465),
            SmtpSecure = preset?.SmtpSecure ?? SecureMode.Ssl,
            IsEnabled = true
        };
    }

    /// <summary>新名优先，找不到再读改名前的 MAILCLIENT_TEST_*，避免测试机配置一夜失效。</summary>
    private static string? ReadEnv(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? Environment.GetEnvironmentVariable(LegacyPrefix + name["ZUNGMAIL_TEST_".Length..]);

    private static int ReadPort(string name, int fallback) =>
        int.TryParse(ReadEnv(name), out var port) && port > 0 ? port : fallback;

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
