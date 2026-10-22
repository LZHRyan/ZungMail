using System.IO;
using System.Linq;
using ZungMail.Application.DependencyInjection;
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
/// 主界面关键旅程的 smoke test：真实依赖注入容器 + 真实 ViewModel + 确定性 IMAP 替身，
/// 覆盖「启动装配 → 同步 → 列表 → 按需下载正文 → 已读回写」整条链路。
/// 不创建窗口（WebView2 与托盘依赖运行环境），因此可在 CI 中稳定执行。
/// </summary>
[Trait("Category", "Ui")]
public sealed class MainViewModelJourneyTests : IDisposable
{
    private readonly string _root;
    private readonly ServiceProvider _provider;

    /// <summary>确定性 IMAP 替身：测试里直接往里加 UID 模拟「服务器上来了一封新信」。</summary>
    private readonly FakeMailbox _mailbox;

    /// <summary>查看邮件源码窗口替身：记录被请求打开的邮件。</summary>
    private readonly FakeMailSourceWindowService _mailSourceWindows;

    public MainViewModelJourneyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zungmail-ui-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppPaths(_root);
        paths.EnsureCreated();

        var mailbox = new FakeMailbox { UidValidity = 11 };
        mailbox.Uids.AddRange(new uint[] { 1, 2 });
        var factory = new FakeImapClientFactory(mailbox);
        _mailbox = mailbox;

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddZungMailInfrastructure();
        services.AddZungMailMail();
        services.AddZungMailApplication();

        // 覆盖默认路径与真实 IMAP 连接，其余装配与 App.OnStartup 保持一致
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<IImapClientFactory>(factory);
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<ClientLockService>();
        services.AddSingleton<IDialogService, NullDialogService>();
        // 查看邮件源码窗口：测试里只记录调用，不创建真实窗口
        _mailSourceWindows = new FakeMailSourceWindowService();
        services.AddSingleton<IMailSourceWindowService>(_mailSourceWindows);
        services.AddSingleton<AttachmentActionService>();
        // 后台收信通知总线：主界面订阅它做「同步后刷新列表」（与 App.OnStartup 保持一致）
        services.AddSingleton<MailReceiveNotifier>();
        // 联系人头像缓存：主界面与阅读窗口都从它取 Gravatar 结果（与 App.OnStartup 保持一致）
        services.AddSingleton<AvatarStore>();
        // 主界面状态栏含升级标识：与 App.OnStartup 一样注册升级协作者与窗口服务（不打开窗口）
        services.AddSingleton<UpdateCoordinator>();
        services.AddSingleton<UpdateInstallService>();
        services.AddSingleton<UpdateWindowService>();
        services.AddSingleton<MainViewModel>();

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
    public async Task 主界面旅程_同步后在列表中展示邮件并按需下载正文()
    {
        await SeedAccountAsync();
        var viewModel = _provider.GetRequiredService<MainViewModel>();

        // 窗口首次上屏时三块区域都应是加载态，而不是空白。
        Assert.True(viewModel.IsAccountsLoading);
        Assert.True(viewModel.IsListLoading);
        Assert.True(viewModel.ShowDetailLoading);

        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsAccountsLoading);
        Assert.False(viewModel.IsListLoading);
        Assert.False(viewModel.ShowDetailLoading);
        Assert.Equal("就绪", viewModel.StatusText);
        Assert.Equal(5, viewModel.QuickViews.Count);
        Assert.Equal("统一收件箱", viewModel.ListTitle);
        Assert.Single(viewModel.Accounts);
        Assert.True(viewModel.IsEmpty);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsSyncing);
        Assert.Equal(2, viewModel.Messages.Count);
        Assert.Equal(2, viewModel.LocalTotal);
        Assert.Contains("共 2 封", viewModel.ListSubtitle);

        // 选中邮件后阅读窗格按需下载正文，并把未读邮件自动标为已读
        await WaitUntilAsync(
            () => viewModel.Detail is { } detail && detail.BodyState == BodyState.Downloaded,
            "阅读窗格未按需下载正文");

        Assert.Contains("hello body", viewModel.Detail!.BodyText);
        Assert.False(viewModel.ShowDetailLoading);
        Assert.False(viewModel.IsBodyLoading);

        await WaitUntilAsync(
            () => viewModel.SelectedRow is { IsUnread: false } && viewModel.LocalUnread == 1,
            "打开邮件后未自动标记为已读");
        Assert.Equal(0, viewModel.PendingSyncCount);
    }

    [Fact]
    public async Task 已读旅程_服务器确认后列表计数与状态提示同步更新()
    {
        await SeedAccountAsync();
        var viewModel = _provider.GetRequiredService<MainViewModel>();
        await viewModel.InitializeAsync();
        await viewModel.RefreshCommand.ExecuteAsync(null);

        // 列表首行被自动选中并已由「打开即已读」逻辑处理完毕
        await WaitUntilAsync(
            () => viewModel.SelectedRow is { IsUnread: false } && viewModel.LocalUnread == 1,
            "初始自动已读未完成");

        Assert.Equal("标为未读", viewModel.ReadToggleText);

        await viewModel.ToggleReadCommand.ExecuteAsync(null);

        Assert.Equal("已标记为未读", viewModel.StatusText);
        Assert.Equal("标为已读", viewModel.ReadToggleText);
        Assert.Equal(2, viewModel.LocalUnread);
        Assert.Equal(0, viewModel.PendingSyncCount);

        await viewModel.ToggleReadCommand.ExecuteAsync(null);

        Assert.Equal("已标记为已读", viewModel.StatusText);
        Assert.Equal("标为未读", viewModel.ReadToggleText);
        Assert.Equal(1, viewModel.LocalUnread);
        Assert.Equal(0, viewModel.PendingSyncCount);
    }

    [Fact]
    public async Task 后台同步收到新邮件后列表自动出现该邮件()
    {
        await SeedAccountAsync();
        var viewModel = _provider.GetRequiredService<MainViewModel>();
        await viewModel.InitializeAsync();
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, viewModel.Messages.Count);

        // 服务器上来了一封新信：后台增量同步把它收进本地库，但此时界面列表还没动
        _mailbox.Uids.Add(3);
        var outcomes = await _provider.GetRequiredService<IMailSyncService>().SyncAllIncrementalAsync();
        Assert.Equal(1, Assert.Single(outcomes).NewMessages);
        Assert.Equal(2, viewModel.Messages.Count);

        // 同步结束的通知一到，列表就该自己出现这封新邮件（以前这里只改状态栏，用户以为没同步）
        _provider.GetRequiredService<MailReceiveNotifier>().PublishCompleted(outcomes);

        await WaitUntilAsync(() => viewModel.Messages.Count == 3, "后台同步后列表没有自动刷新");
        Assert.Contains("邮件 3", viewModel.Messages.Select(row => row.Subject));
        Assert.Contains("共 3 封", viewModel.ListSubtitle);
    }

    [Fact]
    public async Task 查看邮件源码_把当前选中邮件交给源码窗口且不改动状态()
    {
        await SeedAccountAsync();
        var viewModel = _provider.GetRequiredService<MainViewModel>();
        await viewModel.InitializeAsync();
        await viewModel.RefreshCommand.ExecuteAsync(null);

        // 先等「打开即已读」收尾，避免把它的异步更新误算到查看源码头上
        await WaitUntilAsync(
            () => viewModel.SelectedRow is { IsUnread: false } && viewModel.LocalUnread == 1,
            "初始自动已读未完成");

        var row = viewModel.SelectedRow!;
        var unreadBefore = viewModel.LocalUnread;
        var flagsBefore = row.Model.Flags;

        viewModel.ViewMailSourceCommand.Execute(null);

        var request = Assert.Single(_mailSourceWindows.Requests);
        Assert.Equal(row.Id, request.MessageId);
        Assert.Equal(row.Subject, request.Subject);

        // 只读操作：选中行、已读/星标与未读计数都不变
        Assert.Same(row, viewModel.SelectedRow);
        Assert.Equal(flagsBefore, row.Model.Flags);
        Assert.Equal(unreadBefore, viewModel.LocalUnread);
    }

    [Fact]
    public async Task 批量处置条只在多选时出现并替换分页行()
    {
        await SeedAccountAsync();
        var viewModel = _provider.GetRequiredService<MainViewModel>();
        await viewModel.InitializeAsync();
        await viewModel.RefreshCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => viewModel.Messages.Count == 2, "列表未加载");

        // 单选（自动选中的首封）不占分页那一行，分页照常显示
        Assert.False(viewModel.ShowBatchBar);
        Assert.True(viewModel.ShowPagination);

        viewModel.SelectCurrentPageCommand.Execute(null);
        Assert.Equal(2, viewModel.BatchSelectedCount);
        Assert.True(viewModel.ShowBatchBar);
        Assert.False(viewModel.ShowPagination);

        viewModel.ClearBatchSelectionCommand.Execute(null);
        Assert.False(viewModel.ShowBatchBar);
        Assert.True(viewModel.ShowPagination);
    }

    [Fact]
    public async Task 搜索与文件夹列表独立_切换文件夹保留搜索词且再次搜索恢复结果()
    {
        await SeedAccountAsync();
        var viewModel = _provider.GetRequiredService<MainViewModel>();
        await viewModel.InitializeAsync();
        await viewModel.RefreshCommand.ExecuteAsync(null);

        var folder = viewModel.Accounts[0].Folders.First();
        viewModel.SearchText = "邮件";
        await viewModel.SearchCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSearchResults);
        Assert.Equal("搜索结果", viewModel.ListTitle);
        Assert.Contains("当前文件夹", viewModel.ListSubtitle);
        Assert.Equal(2, viewModel.Messages.Count);

        // 选中文件夹只切回普通文件夹列表，不清理搜索框，也不丢失上一次搜索结果状态。
        await viewModel.SelectFolderCommand.ExecuteAsync(folder);

        Assert.False(viewModel.IsSearchResults);
        Assert.Equal("邮件", viewModel.SearchText);
        Assert.Equal(folder.Title, viewModel.ListTitle);
        Assert.Equal(2, viewModel.Messages.Count);

        // 再次触发搜索，并能切换到「所有邮件」范围。
        viewModel.IsSearchScopeAllMail = true;
        await viewModel.SearchCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSearchResults);
        Assert.True(viewModel.IsSearchScopeAllMail);
        Assert.Contains("所有邮件", viewModel.ListSubtitle);
        Assert.Equal(2, viewModel.Messages.Count);
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

    /// <summary>等待 ViewModel 中的异步收尾（详情加载由属性变更命令触发，无法直接 await）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), message);
    }

    private sealed class NullDialogService : IDialogService
    {
        public bool ShowCompose(ComposeRequest request) => false;

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
