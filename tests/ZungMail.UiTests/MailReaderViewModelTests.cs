using System.IO;
using ZungMail.Application.DependencyInjection;
using ZungMail.Application.Services;
using ZungMail.App.Services;
using ZungMail.App.ViewModels;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.DependencyInjection;
using ZungMail.Infrastructure.Storage;
using ZungMail.Mail;
using ZungMail.Mail.DependencyInjection;
using ZungMail.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 独立阅读窗口（双击邮件列表弹出）的视图模型契约：只读一封邮件、先邮件头上屏再按需下载正文、
/// 正文与附件渲染参数跟随设置。不创建真实窗口（WebView2 依赖运行环境），可在 CI 稳定执行。
/// </summary>
[Trait("Category", "Ui")]
public sealed class MailReaderViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly ServiceProvider _provider;
    private readonly FakeMailbox _mailbox;

    public MailReaderViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-reader-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();

        var mailbox = new FakeMailbox { UidValidity = 13 };
        mailbox.Uids.AddRange(new uint[] { 1, 2 });
        _mailbox = mailbox;

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddZungMailInfrastructure();
        services.AddZungMailMail();
        services.AddZungMailApplication();

        // 与 App.OnStartup 的装配保持一致，仅替换路径与 IMAP 连接
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<IImapClientFactory>(new FakeImapClientFactory(mailbox));
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<IDialogService, RecordingDialogService>();
        // 查看邮件源码窗口：测试里只记录调用，不创建真实窗口
        services.AddSingleton<IMailSourceWindowService>(new FakeMailSourceWindowService());
        services.AddSingleton<AttachmentActionService>();
        services.AddSingleton<AvatarStore>();
        // 阅读区「AI 总结」子视图模型：与 App.OnStartup 保持一致（测试里不触发推理）
        services.AddSingleton<MailSummaryViewModel>();
        services.AddSingleton<MailReaderViewModel>();

        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
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
    public async Task 独立阅读窗口_先邮件头上屏再按需下载正文()
    {
        await SeedAccountAsync();
        await _provider.GetRequiredService<SynchronizationCoordinator>().SyncAsync();

        var page = await _provider.GetRequiredService<IMessageRepository>()
            .QueryAsync(new MessageQuery { Take = 10 });
        Assert.NotEmpty(page.Items);

        var reader = _provider.GetRequiredService<MailReaderViewModel>();
        await reader.OpenAsync(page.Items[0].Id);

        Assert.True(reader.HasDetail);
        Assert.False(reader.ShowLoading);
        Assert.False(reader.ShowPlaceholder);
        Assert.False(reader.IsBodyLoading);
        Assert.Equal(BodyState.Downloaded, reader.Detail!.BodyState);
        Assert.Contains("hello body", reader.Detail.BodyText);
        Assert.Equal(reader.Detail.SubjectDisplay, reader.WindowTitle);

        // 正文渲染走与主窗口相同的文档构建器，独立窗口不能出现空白正文
        Assert.Contains("hello body", reader.BodyHtmlDocument);
        Assert.NotEmpty(reader.EmlDirectory);

        // 阅读窗口不持有列表状态：打开后不存在选中行、批量选择这类概念
        Assert.False(reader.HasAttachments);
        Assert.False(reader.HasTags);
    }

    [Fact]
    public async Task 独立阅读窗口_切换邮件后只保留后一封的内容()
    {
        await SeedAccountAsync();
        await _provider.GetRequiredService<SynchronizationCoordinator>().SyncAsync();

        var page = await _provider.GetRequiredService<IMessageRepository>()
            .QueryAsync(new MessageQuery { Take = 10 });
        Assert.True(page.Items.Count >= 2);

        var reader = _provider.GetRequiredService<MailReaderViewModel>();
        await reader.OpenAsync(page.Items[0].Id);
        var firstId = reader.Detail!.Id;

        await reader.OpenAsync(page.Items[1].Id);

        Assert.NotEqual(firstId, reader.Detail!.Id);
        Assert.Equal(reader.Detail.SubjectDisplay, reader.WindowTitle);
        Assert.False(reader.IsLoading);
        Assert.False(reader.IsBodyLoading);
    }

    [Fact]
    public async Task 独立阅读窗口_可以直接回复与转发()
    {
        await SeedAccountAsync();
        await _provider.GetRequiredService<SynchronizationCoordinator>().SyncAsync();

        var page = await _provider.GetRequiredService<IMessageRepository>()
            .QueryAsync(new MessageQuery { Take = 10 });
        Assert.NotEmpty(page.Items);

        var reader = _provider.GetRequiredService<MailReaderViewModel>();
        await reader.OpenAsync(page.Items[0].Id);

        Assert.True(reader.CanReplyOrForward);

        var dialogs = (RecordingDialogService)_provider.GetRequiredService<IDialogService>();

        await reader.ReplyCommand.ExecuteAsync(null);
        Assert.NotNull(dialogs.LastCompose);
        Assert.Equal(ComposeMode.Reply, dialogs.LastCompose!.Mode);
        Assert.Contains("mc-quote", dialogs.LastCompose.Body);

        await reader.ForwardCommand.ExecuteAsync(null);
        Assert.Equal(ComposeMode.Forward, dialogs.LastCompose!.Mode);
        Assert.StartsWith("Fwd: ", dialogs.LastCompose.Subject);
    }

    [Fact]
    public void 独立阅读窗口_陌生提示条跟随设置开关()
    {
        var reader = _provider.GetRequiredService<MailReaderViewModel>();
        var settings = _provider.GetRequiredService<ISettingsStore>();

        reader.Detail = new EmailDetail { Id = 1, FromEmail = "newagent@example.com", IsStranger = true };
        Assert.True(reader.ShowDetailStranger);

        reader.Detail = new EmailDetail { Id = 2, FromEmail = "old@partner.com" };
        Assert.False(reader.ShowDetailStranger);

        try
        {
            settings.Current.MarkStrangerSenders = false;
            reader.Detail = new EmailDetail { Id = 1, FromEmail = "newagent@example.com", IsStranger = true };
            Assert.False(reader.ShowDetailStranger);
        }
        finally
        {
            settings.Current.MarkStrangerSenders = true;
        }
    }

    [Fact]
    public async Task 独立阅读窗口_形近域名显示钓鱼警示且跟随开关()
    {
        // 账户是 me@example.com：发件人用 1 冒充 l 的 examp1e.com
        _mailbox.Senders[1] = "ops@examp1e.com";

        await SeedAccountAsync();
        await _provider.GetRequiredService<SynchronizationCoordinator>().SyncAsync();

        var page = await _provider.GetRequiredService<IMessageRepository>()
            .QueryAsync(new MessageQuery { Take = 10 });
        var lookalike = page.Items.Single(item => item.FromEmail == "ops@examp1e.com");
        var normal = page.Items.Single(item => item.FromEmail == "sender@example.com");

        var reader = _provider.GetRequiredService<MailReaderViewModel>();
        await reader.OpenAsync(lookalike.Id);

        Assert.True(reader.ShowDetailPhishing);
        Assert.Contains("example.com", reader.DetailPhishingHint);

        await reader.OpenAsync(normal.Id);
        Assert.False(reader.ShowDetailPhishing);

        var settings = _provider.GetRequiredService<ISettingsStore>();
        settings.Current.PhishingDomainWarning = false;
        try
        {
            await reader.OpenAsync(lookalike.Id);
            Assert.False(reader.ShowDetailPhishing);
        }
        finally
        {
            settings.Current.PhishingDomainWarning = true;
        }
    }

    private async Task SeedAccountAsync()
    {
        await _provider.GetRequiredService<ISettingsStore>().LoadAsync();
        await _provider.GetRequiredService<IAccountRepository>().AddAsync(new Account
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
    }

    /// <summary>记录撰写请求的对话框替身：验证阅读窗口的回复 / 转发确实走了同一套装配。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public ComposeRequest? LastCompose { get; private set; }

        public bool ShowCompose(ComposeRequest request)
        {
            LastCompose = request;
            return false;
        }

        public bool ShowAccountEditor(Account? account) => false;

        public bool ShowSettings(string? sectionKey = null, long? accountId = null) => false;

        public bool ShowOutbox() => false;

        public bool ShowCleanup(CleanupScope? scope = null) => false;

        public bool ShowAddressBook() => false;

        public FollowUpCreateRequest? ShowFollowUpEditor(EmailDetail detail, string selfEmail) => null;

        public FollowUpActionRequest? ShowFollowUps() => null;

        public bool ShowShipmentCase(long caseId, Action<string>? applySearch = null) => false;

        public bool ShowFolderEditor(FolderEditRequest request) => false;

        public MailFolder? PickMoveTarget(IReadOnlyList<MoveTargetItem> targets, MailFolder? current) => null;

        public bool ShowAttachmentPreview(string fileName, string path) => false;

        public void ShowDiagnosticsReport(DiagnosticsReport report)
        {
        }

        public bool Confirm(
            string title,
            string message,
            string primaryText = "确定",
            string? detail = null,
            DialogIcon icon = DialogIcon.Question,
            bool danger = false) => true;

        public DialogChoice Ask(MessageDialogOptions options) => DialogChoice.Cancel;

        public void Alert(string title, string message, DialogIcon icon = DialogIcon.Info, string? detail = null)
        {
        }
    }
}
