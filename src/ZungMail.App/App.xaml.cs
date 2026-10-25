using System.Windows;
using System.Windows.Threading;
using ZungMail.Application.DependencyInjection;
using ZungMail.App.Services;
using ZungMail.App.ViewModels;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Infrastructure.DependencyInjection;
using ZungMail.Infrastructure.Logging;
using ZungMail.Infrastructure.Storage;
using ZungMail.Mail.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZungMail.App;

/// <summary>
/// 应用入口：装配依赖注入容器、初始化本地存储与主题，随后显示主窗口并触发启动同步。
/// </summary>
public partial class App : System.Windows.Application
{
    // Local 命名空间限制在当前登录会话；避免不同 Windows 用户之间互相阻塞。
    private const string SingleInstanceMutexName = @"Local\ZungMail.Desktop.SingleInstance.v1";

    /// <summary>第二个实例用它叫醒第一个实例（把主窗口调到前台），自己立刻退出。</summary>
    private const string ActivateEventName = @"Local\ZungMail.Desktop.Activate.v1";

    private IHost? _host;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _activateSignal;
    private Thread? _activateListener;
    private volatile bool _activateStopped;

    /// <summary>启动计时：从进程入口到首帧、到界面可交互的每一段，用来定位「启动慢」。</summary>
    private readonly List<string> _startupStages = new();

    /// <summary>摘要引擎插件的加载结果（宿主起来后再落日志）。</summary>
    private IReadOnlyList<string> _summaryModuleReports = Array.Empty<string>();

    /// <summary>记录一段启动耗时：先落一行日志（卡住时最后一行就是卡点），再累积到汇总行。</summary>
    private void MarkStartupStage(string stage)
    {
        _startupStages.Add($"{stage} {StartupTrace.ElapsedMs}ms");
        StartupTrace.Mark(stage);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 必须最先开启：后面的每一段都会各自落一行日志，进程卡在哪一段一眼可见
        StartupTrace.Enable();

        // 运行环境快照（系统 / CPU / 内存 / 数据目录所在盘）：只读注册表与文件大小，几毫秒
        StartupEnvironment.LogCore();

        if (!TryAcquireSingleInstance())
        {
            // 已有实例在跑：只把信号丢给它并退出，不弹模态框——
            // 弹框会让这个进程带着整套 WPF 运行时（约 110 MB）一直挂在那里等用户点确定。
            Shutdown();
            return;
        }

        MarkStartupStage("单实例");
        StartActivationListener();

        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        // 数据目录迁移必须在构建宿主、打开任何数据库连接之前完成
        await RunPendingDataMigrationAsync().ConfigureAwait(true);
        MarkStartupStage("数据目录");

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(new AppPaths()));
        builder.Services.AddZungMailInfrastructure();

        // 覆盖基础设施层的数据库初始化注册：把内部分段耗时接到启动计时上
        // （Infrastructure 不引用 App 层，只能在这里注入回调；同类型后注册者生效）。
        builder.Services.AddSingleton(sp =>
        {
            var initializer = new DatabaseInitializer(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                StartupTrace.Mark);
            initializer.Initialize();
            return initializer;
        });
        builder.Services.AddZungMailMail();
        builder.Services.AddZungMailApplication();

        // 摘要引擎插件：本地小模型由独立程序集通过反射注册；失败只降级，不影响启动
        _summaryModuleReports = SummaryModuleCatalog.Register(builder.Services);
        builder.Services.AddSingleton<ThemeManager>();
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<AttachmentActionService>();

        // 在线升级：状态机 + 下载/安装引导 + 升级窗口（单实例）
        builder.Services.AddSingleton<UpdateCoordinator>();
        builder.Services.AddSingleton<UpdateInstallService>();
        builder.Services.AddSingleton<UpdateWindowService>();
        builder.Services.AddSingleton<IStartupLaunchService, StartupLaunchService>();
        builder.Services.AddSingleton<MailReceiveNotifier>();
        builder.Services.AddSingleton<ClientLockService>();
        builder.Services.AddSingleton<GlobalHotkeyService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        // 独立阅读窗口：视图模型与窗口宿主都是单例，双击只切换内容、不重复开窗
        builder.Services.AddSingleton<MailReaderViewModel>();
        builder.Services.AddSingleton<MailReaderWindowService>();

        // 邮件摘要卡片：两个阅读窗口各一份状态（transient，互不影响）
        builder.Services.AddTransient<MailSummaryViewModel>();

        // 查看邮件源码：主窗口列表右键与阅读窗口共用同一个单实例窗口
        builder.Services.AddSingleton<MailSourceViewModel>();
        builder.Services.AddSingleton<IMailSourceWindowService, MailSourceWindowService>();

        builder.Services.AddSingleton<TrayIconService>();

        // 定时发送（FR-08）在后台按点投递，不依赖用户打开发件箱手动触发
        builder.Services.AddHostedService<OutboxScheduler>();

        // 按用户设置的间隔增量收信（收件箱每轮必刷 + 其余文件夹滚动），并由托盘提示新增邮件
        builder.Services.AddHostedService<InboxSyncScheduler>();

        // IMAP IDLE 推送：收件箱一有新邮件立刻同步（秒级），掉线自动回退到上面的定时轮询
        builder.Services.AddHostedService<InboxIdleScheduler>();

        // 启动数分钟后自动检测新版本；失败静默，只更新状态与日志
        builder.Services.AddHostedService<UpdateCheckScheduler>();

        // 自动定期清理历史邮件，并回收超期隔离数据
        builder.Services.AddHostedService<AutoCleanupScheduler>();

        // 运行日志只保留最近 N 天（默认 7 天），超期自动删除，避免 logs 目录无限增长
        builder.Services.AddHostedService<LogRetentionScheduler>();

        // 联系人头像：启动后闲时一次性查 Gravatar（每轮最多 300 个地址，进程内只尝试一次）
        builder.Services.AddSingleton<AvatarStore>();
        builder.Services.AddHostedService<AvatarLoaderScheduler>();

        // 待办到期提醒：后台扫描 + 托盘气泡，避免同一事项重复提醒
        builder.Services.AddHostedService<TodoReminderScheduler>();

        // 等待回复巡检：到点检查谁没回，提醒一次后停下，由用户决定是否继续跟进
        builder.Services.AddHostedService<FollowUpScheduler>();

        _host = builder.Build();
        MarkStartupStage("容器构建");

        // 旧品牌数据目录的接管 / 搬迁说明：读指针发生在任何 logger 就绪之前，这里补落一行日志
        foreach (var notice in DataDirectoryPointer.DrainNotices())
        {
            _host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ZungMail.Storage")
                .LogInformation("{Notice}", notice);
        }

        // 摘要引擎插件加载结果：程序集缺失或原生库坏掉时，这里能直接看到是哪一步失败
        foreach (var report in _summaryModuleReports)
        {
            _host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ZungMail.Summary")
                .LogInformation("摘要引擎插件：{Report}", report);
        }

        // 改名遗留：旧「开机自启」值指向已被卸载的 MailClient.exe，存在则迁移到本程序
        _host.Services.GetRequiredService<IStartupLaunchService>().MigrateLegacyValue();

        // 数据目录准备（含 ACL 收紧）也走首次解析的构造逻辑，单独计一段
        _ = _host.Services.GetRequiredService<IAppPaths>();
        MarkStartupStage("数据目录准备");

        // 数据库建表 / 迁移是同步 SQLite 调用，就发生在第一次解析仓储时；
        // 显式先解析一次，把这段耗时从「宿主启动」里单独拆出来。
        _ = _host.Services.GetRequiredService<DatabaseInitializer>();
        MarkStartupStage("数据库初始化");

        var settings = _host.Services.GetRequiredService<ISettingsStore>();
        await settings.LoadAsync().ConfigureAwait(true);
        MarkStartupStage("读设置");

        // 与启动耗时相关的设置项（同步策略 / 分页 / 会话模式 / 自动清理）
        StartupEnvironment.LogSettings(settings.Current);

        var theme = _host.Services.GetRequiredService<ThemeManager>();
        theme.Apply();
        MarkStartupStage("主题");

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        MarkStartupStage("窗口显示");

        // 先让首帧（标题栏 / 工具栏 / 加载占位）真正画出来，再去读账户、文件夹与邮件。
        // SQLite 的异步接口多数会同步完成，不主动让出调度器时整段启动流程会占满 UI 线程。
        await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        MarkStartupStage("首帧");

        // 首帧之后再装 UI 占用探针：心跳要等窗口真的画出来才有意义，也不占启动关键路径
        UiThreadProbe.Attach(Dispatcher);

        // 首帧之后再补图形 / WebView2：拉 WinForms + WPF 媒体 + WebView2 探测要上百毫秒，
        // 不放在「窗口出现」之前的关键路径上。渲染 Tier 与 DPI 直接决定窗口绘制快慢。
        StartupEnvironment.LogGraphics();

        // 托盘常驻：关闭主窗口时隐藏而非退出，保证后台定时发送继续生效
        _host.Services.GetRequiredService<TrayIconService>().Attach(window);

        // 全局热键：显示/隐藏主界面（Foxmail 的「显示主窗口」热键）
        var hotkey = _host.Services.GetRequiredService<GlobalHotkeyService>();
        if (hotkey.Attach(window))
        {
            hotkey.Pressed += (_, _) => ToggleMainWindow(window);
            var applied = hotkey.Apply(settings.Current.GlobalHotkeyEnabled, settings.Current.GlobalHotkey);
            if (!applied.Ok && settings.Current.GlobalHotkeyEnabled)
            {
                AppLog.Error("全局热键注册失败：" + applied.Message);
            }
        }
        MarkStartupStage("托盘热键");

        // 宿主服务放到窗口显示、托盘就绪之后启动：首轮里的 DB 调用是「假异步」（同步完成），
        // 早启动会把首屏查询挤在同一个 UI 线程上；各 ExecuteAsync 首行已 await Task.Yield，
        // StartAsync 只等到第一次让出就返回，不会把首轮搬回这里执行。
        await _host.StartAsync().ConfigureAwait(true);
        MarkStartupStage("宿主启动");

        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        await viewModel.InitializeAsync().ConfigureAwait(true);
        MarkStartupStage("账户与列表");

        // 启动计时汇总：一行读出每一段耗时，定位「启动慢」到底慢在哪一段
        _host.Services.GetRequiredService<ILogger<App>>().LogInformation(
            "启动计时（从进程入口累计）：进程创建→入口 {ProcessMs}ms · {Stages}",
            StartupTrace.ProcessStartToEntryMs,
            string.Join(" · ", _startupStages));

        if (settings.Current.SyncOnLaunch)
        {
            // 启动即增量同步，但不阻塞界面（同步引擎内部在后台线程执行）
            _ = viewModel.RefreshCommand.ExecuteAsync(null);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                // 设置项「退出时清除正文缓存」在此落地：只清正文与附件记录，邮件头保留
                var settings = _host.Services.GetRequiredService<ISettingsStore>();
                if (settings.Current.ClearBodyCacheOnExit)
                {
                    var messages = _host.Services.GetRequiredService<IMessageRepository>();
                    await messages.ClearBodyCacheAsync().ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("退出时清理正文缓存失败", ex);
            }

            await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            _host.Dispose();
        }

        ReleaseSingleInstance();
        StopActivationListener();
        base.OnExit(e);
    }

    /// <summary>全局热键：主窗口在前台时隐藏，否则显示并激活。</summary>
    private static void ToggleMainWindow(Window window)
    {
        if (window.IsVisible && window.WindowState != WindowState.Minimized && window.IsActive)
        {
            window.Hide();
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        // 从其它前台程序切回来时，Activate 偶尔不足以置顶，用一次 Topmost 脉冲兜底。
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    /// <summary>
    /// 处理「已排入下次启动」的数据目录迁移：显示进度窗口 → 复制/校验/改库内路径 → 切指针。
    /// 失败时指针回退到原目录并提示，客户端继续用旧数据正常启动。
    /// <para>
    /// 在这之前先做「改名后旧数据目录一次性接管」：必须早于任何指针读取方的缓存与数据库连接，
    /// 并且只在这里做一次（读指针本身不带副作用，避免测试或日志采集误搬用户数据）。
    /// </para>
    /// </summary>
    private async Task RunPendingDataMigrationAsync()
    {
        try
        {
            _ = new DataDirectoryPointer().EnsureLegacyAdopted();
        }
        catch (Exception ex)
        {
            // 接管失败不影响启动：读指针会退回新默认目录或旧指针
            AppLog.Error("接管旧数据目录失败，按现有指针继续启动", ex);
        }

        IDataDirectoryService service;
        DataDirectoryStatus status;
        try
        {
            service = new DataDirectoryService();
            status = service.GetStatus();
        }
        catch (Exception ex)
        {
            AppLog.Error("读取数据目录指针失败，使用默认目录启动", ex);
            return;
        }

        if (!status.HasPending || status.PendingTarget is not { } target)
        {
            return;
        }

        var window = new DataMigrationWindow(status.CurrentDirectory, target);

        // 迁移窗口是此时唯一的窗口：默认的 OnLastWindowClose 会在它关闭时直接退出应用，
        // 所以迁移期间切到显式关闭，窗口关掉后再恢复。
        var previousShutdownMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Show();

        DataMigrationResult result;
        try
        {
            // 让进度窗口先完成一次渲染，再开始复制
            await Dispatcher.Yield(DispatcherPriority.Background);

            var progress = new Progress<DataMigrationProgress>(window.Report);
            result = await service.MigratePendingAsync(progress).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error("数据目录迁移异常", ex);
            result = new DataMigrationResult(false, "迁移失败：" + ex.Message, status.CurrentDirectory);
        }
        finally
        {
            window.MarkCompleted();
            ShutdownMode = previousShutdownMode;
        }

        if (result.Success)
        {
            AppLog.Info("数据目录已迁移到 " + (result.Target ?? target));
            return;
        }

        // 指针回到原目录，避免下一次启动落到半成品目录
        try { service.CancelPending(); }
        catch (Exception ex) { AppLog.Error("回退数据目录指针失败", ex); }

        MessageDialog.Alert(
            "数据目录迁移失败",
            result.Message,
            DialogIcon.Error,
            detail: "客户端将继续使用原数据目录：" + Environment.NewLine + status.CurrentDirectory);
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (createdNew)
            {
                _ownsSingleInstanceMutex = true;
                return true;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;

            // 先叫醒已有实例（恢复主窗口），再让本进程退出；不再弹「已在运行」对话框
            SignalExistingInstance();
            return false;
        }
        catch (Exception ex)
        {
            // 无法创建互斥锁时宁可继续启动，不把系统策略/权限问题变成无法使用客户端。
            AppLog.Error("创建单实例互斥锁失败，将继续启动", ex);
            return true;
        }
    }

    /// <summary>第二个实例：通过命名事件请求第一个实例把主窗口调到前台。</summary>
    private static void SignalExistingInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivateEventName, out var signal)) return;

            using (signal) signal.Set();
        }
        catch (Exception ex)
        {
            // 事件句柄不可用时静默退出：至少不会留下一个没有窗口的进程
            AppLog.Error("通知已有实例失败", ex);
        }
    }

    /// <summary>
    /// 第一个实例：后台线程等待「再启动一次」的信号，收到后把主窗口拉到前台。
    /// 用后台线程 + Dispatcher 而不是 UI 定时器，空闲时零开销。
    /// </summary>
    private void StartActivationListener()
    {
        try
        {
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception ex)
        {
            AppLog.Error("创建单实例激活信号失败", ex);
            return;
        }

        _activateListener = new Thread(() =>
        {
            while (!_activateStopped)
            {
                if (!_activateSignal.WaitOne(TimeSpan.FromSeconds(1))) continue;
                if (_activateStopped) break;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MainWindow is { } window) BringToFront(window);
                }));
            }
        })
        {
            IsBackground = true,
            Name = "ZungMail.ActivateListener"
        };
        _activateListener.Start();
    }

    private void StopActivationListener()
    {
        _activateStopped = true;

        try { _activateSignal?.Set(); } catch (Exception) { /* 已释放 */ }
        try { _activateListener?.Join(TimeSpan.FromSeconds(1)); } catch (Exception) { /* 线程已退出 */ }

        _activateListener = null;
        _activateSignal?.Dispose();
        _activateSignal = null;
    }

    /// <summary>显示并前置主窗口（托盘隐藏、最小化都能恢复）。</summary>
    private static void BringToFront(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();

        // 从其它前台程序切回来时，Activate 偶尔不足以置顶，用一次 Topmost 脉冲兜底。
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private void ReleaseSingleInstance()
    {
        if (_singleInstanceMutex is null) return;

        try
        {
            if (_ownsSingleInstanceMutex) _singleInstanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 已被系统释放或当前线程不再拥有时，无需阻止退出。
        }
        finally
        {
            _ownsSingleInstanceMutex = false;
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("未处理的域异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        AppLog.Error("界面线程未处理异常", args.Exception);
        MessageBox.Show(
            $"{args.Exception.Message}\n\n详细信息已写入日志目录。",
            "ZungMail 出现异常",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        args.Handled = true;
    }
}
