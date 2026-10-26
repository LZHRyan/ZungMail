using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.Application.Services;
using ZungMail.App.Services;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using ZungMail.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 主窗口视图模型：文件夹树 + 邮件列表 + 阅读窗格 + 状态栏（三栏外壳）。
/// 列表查询始终分页（NFR-P4），会话模式走 <see cref="IMessageRepository.QueryThreadsAsync"/>（FR-05）。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly IContactRepository _contacts;
    private readonly ISettingsStore _settings;
    private readonly SynchronizationCoordinator _syncCoordinator;
    private readonly MessageDetailLoader _detailLoader;
    private readonly MailActionService _mailActions;
    private readonly MailFolderService _folderService;
    private readonly MailTagService _tagService;
    private readonly MailTodoService _todoService;
    private readonly MailFollowUpService _followUpService;
    private readonly MailReferenceService _referenceService;
    private readonly IMailReferenceRepository _referenceRepository;
    private readonly IShipmentRepository _shipments;
    private readonly SenderFilterService _senderFilters;
    private readonly ClientLockService _clientLock;

    /// <summary>后台收信通知总线：同步结束后用它刷新列表，不再只改状态栏。</summary>
    private readonly MailReceiveNotifier _receiveNotifier;
    private readonly IMailContextRepository _context;
    private readonly IMessageNoteRepository _notes;
    private readonly IReadReceiptService _receiptService;
    private readonly IReadReceiptRepository _receipts;
    private readonly IStorageMonitor _storageMonitor;
    private readonly IMailImportService _import;
    private readonly ThemeManager _theme;
    private readonly IDialogService _dialogs;
    private readonly AttachmentActionService _attachments;

    /// <summary>查看邮件源码窗口（与阅读窗口共用同一个单实例）。</summary>
    private readonly IMailSourceWindowService _mailSourceWindows;

    /// <summary>「AI 总结」卡片状态（主窗口右栏自己一份）。</summary>
    public MailSummaryViewModel Summary { get; }

    /// <summary>联系人头像缓存（Gravatar 懒加载的结果）：邮件头与侧栏账户按地址取画刷。</summary>
    private readonly AvatarStore _avatars;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>内联附件的落盘目录，供正文文档把 file:// 重写为虚拟主机。</summary>
    private readonly string _emlDirectory;

    /// <summary>本封邮件是否已放行远程图片（不持久化，切换邮件即复位）。</summary>
    private bool _loadRemoteImages;

    /// <summary>
    /// 当前这次「打开邮件」的取消源：选中下一封时取消上一封仍在进行的正文下载，
    /// 避免快速连点十封就开十条 IMAP 连接、各自下一整封。
    /// </summary>
    private CancellationTokenSource? _detailCts;

    private QuickViewItem? _currentQuickView;
    private FolderNode? _currentFolder;
    private MailTag? _currentTag;
    private int _loadVersion;

    /// <summary>从跟进 / 案件窗口定位到的邮件：设置后列表只显示这一封。</summary>
    private long? _pinnedEmailId;

    /// <summary>批量修改查询条件时挂起重载，避免多次触发列表查询。</summary>
    private bool _suspendReload;

    /// <summary>程序化切换搜索范围时挂起重载，避免先清空范围再设值触发两次查询。</summary>
    private bool _suppressSearchReload;


    /// <summary>标签快捷键缓存：窗口级按键处理用它判断组合键是否已绑定，避免吞掉普通快捷键。</summary>
    private IReadOnlyList<MailTag> _tagShortcutCache = Array.Empty<MailTag>();

    public MainViewModel(
        IAccountRepository accounts,
        IFolderRepository folders,
        IMessageRepository messages,
        IContactRepository contacts,
        ISettingsStore settings,
        SynchronizationCoordinator syncCoordinator,
        MessageDetailLoader detailLoader,
        MailActionService mailActions,
        MailFolderService folderService,
        MailTagService tagService,
        MailTodoService todoService,
        MailFollowUpService followUpService,
        MailReferenceService referenceService,
        IMailReferenceRepository referenceRepository,
        IShipmentRepository shipments,
        SenderFilterService senderFilters,
        ClientLockService clientLock,
        IMailContextRepository context,
        IMessageNoteRepository notes,
        IReadReceiptService receiptService,
        IReadReceiptRepository receipts,
        IStorageMonitor storageMonitor,
        IMailImportService import,
        MailReceiveNotifier receiveNotifier,
        IOutboxProcessor outboxProcessor,
        ThemeManager theme,
        IDialogService dialogs,
        AttachmentActionService attachments,
        IMailSourceWindowService mailSourceWindows,
        MailSummaryViewModel summary,
        IAppPaths paths,
        AvatarStore avatarStore,
        UpdateCoordinator updateCoordinator,
        UpdateWindowService updateWindowService,
        ILogger<MainViewModel> logger)
    {
        _accounts = accounts;
        _folders = folders;
        _messages = messages;
        _contacts = contacts;
        _settings = settings;
        _syncCoordinator = syncCoordinator;
        _detailLoader = detailLoader;
        _mailActions = mailActions;
        _folderService = folderService;
        _tagService = tagService;
        _todoService = todoService;
        _followUpService = followUpService;
        _referenceService = referenceService;
        _referenceRepository = referenceRepository;
        _shipments = shipments;
        _senderFilters = senderFilters;
        _clientLock = clientLock;
        _receiveNotifier = receiveNotifier;
        _context = context;
        _notes = notes;
        _receiptService = receiptService;
        _receipts = receipts;
        _isContextSidebarVisible = settings.Current.ShowContextSidebar;
        _isQuickViewsExpanded = settings.Current.SidebarQuickViewsExpanded;
        _isTagsExpanded = settings.Current.SidebarTagsExpanded;
        _storageMonitor = storageMonitor;
        _import = import;
        _theme = theme;
        _dialogs = dialogs;
        _attachments = attachments;
        _mailSourceWindows = mailSourceWindows;
        Summary = summary;
        _avatars = avatarStore;
        _emlDirectory = paths.EmlDirectory;
        _logger = logger;

        _syncCoordinator.ProgressChanged += OnSyncProgress;
        _clientLock.LockStateChanged += OnLockStateChanged;
        _tagService.TagsChanged += OnTagsChanged;
        _receiveNotifier.SyncCompleted += OnBackgroundSyncCompleted;
        _avatars.Changed += OnAvatarsChanged;

        // 发送已改成「入发件箱即关窗」，结果只能靠状态栏 / 托盘反馈（写邮件窗口那时已经关了）
        outboxProcessor.ItemSent += OnOutboxItemSent;
        outboxProcessor.ItemFailed += OnOutboxItemFailed;

        StartAutoLockTimer();

        InitializeUpdate(updateCoordinator, updateWindowService);

        BuildQuickViews();
    }

    // ---------------- 客户端锁定 ----------------

    /// <summary>锁定遮罩是否可见。</summary>
    public bool IsLocked => _clientLock.IsLocked;

    /// <summary>解锁失败提示。</summary>
    [ObservableProperty]
    private string _unlockError = "";

    [ObservableProperty]
    private string _unlockPassword = "";

    private DispatcherTimer? _autoLockTimer;

    [RelayCommand]
    private void LockClient()
    {
        if (!_clientLock.IsConfigured)
        {
            StatusText = "尚未启用客户端锁定，请先在 设置 → 通用 → 安全 中设置密码";
            return;
        }

        _clientLock.Lock();
    }

    /// <summary>尝试用输入的密码解锁；成功返回 true。</summary>
    public bool TryUnlockClient(string? password)
    {
        if (_clientLock.TryUnlock(password))
        {
            UnlockError = "";
            UnlockPassword = "";
            StatusText = "已解锁";
            return true;
        }

        UnlockError = "密码不正确，请重试";
        return false;
    }

    private void OnLockStateChanged(object? sender, bool locked)
    {
        // 服务可能在后台线程触发（例如定时器），统一切回 UI 线程更新绑定。
        if (!UiDispatcher.CheckAccess())
        {
            UiDispatcher.Invoke(() => OnLockStateChanged(sender, locked));
            return;
        }

        if (locked)
        {
            UnlockError = "";
            UnlockPassword = "";
        }

        OnPropertyChanged(nameof(IsLocked));
    }

    private static Dispatcher UiDispatcher
        => System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>每 20 秒检查一次系统空闲时间，达到设定分钟数则自动锁定。</summary>
    private void StartAutoLockTimer()
    {
        _autoLockTimer = new DispatcherTimer(DispatcherPriority.Background, UiDispatcher)
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _autoLockTimer.Tick += (_, _) =>
        {
            try
            {
                if (_clientLock.ShouldAutoLock()) _clientLock.Lock();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "自动锁定检查失败");
            }
        };
        _autoLockTimer.Start();
    }

    // ---------------- 集合 ----------------

    public ObservableCollection<QuickViewItem> QuickViews { get; } = new();

    public ObservableCollection<AccountNode> Accounts { get; } = new();

    /// <summary>侧边栏标签列表（收藏的标签视图入口）。</summary>
    public ObservableCollection<MailTag> SidebarTags { get; } = new();

    public ObservableRangeCollection<MailRow> Messages { get; } = new();

    // ---------------- 状态 ----------------

    [ObservableProperty]
    private MailRow? _selectedRow;

    [ObservableProperty]
    private EmailDetail? _detail;

    /// <summary>正文文档缓存：小正文就地组装，大正文（如 8MB 的水单邮件）在线程池组装。</summary>
    private readonly MailBodyDocumentCache _bodyDocument = new();

    /// <summary>当前阅读邮件的标签，用于邮件头展示。</summary>
    [ObservableProperty]
    private IReadOnlyList<MailTag> _detailTags = Array.Empty<MailTag>();

    /// <summary>当前邮件的本地备注（批注）。</summary>
    [ObservableProperty]
    private string _noteText = "";

    public bool HasNote => !string.IsNullOrWhiteSpace(NoteText);

    /// <summary>当前邮件是否请求了阅读收条且尚未回复。</summary>
    [ObservableProperty]
    private bool _canSendReadReceipt;

    partial void OnNoteTextChanged(string value) => OnPropertyChanged(nameof(HasNote));

    [RelayCommand]
    private async Task SaveNoteAsync()
    {
        if (Detail is not { } detail) return;

        try
        {
            await _notes.SaveAsync(detail.Id, NoteText);
            OnPropertyChanged(nameof(HasNote));
            StatusText = string.IsNullOrWhiteSpace(NoteText) ? "已清除邮件备注" : "邮件备注已保存（仅本地）";
        }
        catch (Exception ex)
        {
            StatusText = "保存备注失败：" + ex.Message;
        }
    }

    /// <summary>右侧上下文侧边栏（往来邮件 / 同发件人附件）。</summary>
    [ObservableProperty]
    private bool _isContextSidebarVisible = true;

    [ObservableProperty]
    private IReadOnlyList<CorrespondenceItem> _correspondence = Array.Empty<CorrespondenceItem>();

    [ObservableProperty]
    private IReadOnlyList<SenderAttachmentItem> _senderAttachments = Array.Empty<SenderAttachmentItem>();

    public bool HasCorrespondence => Correspondence.Count > 0;

    public bool HasSenderAttachments => SenderAttachments.Count > 0;

    public string ContextSidebarSummary => $"往来 {Correspondence.Count} · 附件 {SenderAttachments.Count}";

    partial void OnCorrespondenceChanged(IReadOnlyList<CorrespondenceItem> value)
    {
        OnPropertyChanged(nameof(HasCorrespondence));
        OnPropertyChanged(nameof(ContextSidebarSummary));
    }

    partial void OnSenderAttachmentsChanged(IReadOnlyList<SenderAttachmentItem> value)
    {
        OnPropertyChanged(nameof(HasSenderAttachments));
        OnPropertyChanged(nameof(ContextSidebarSummary));
    }

    [ObservableProperty]
    private CorrespondenceItem? _selectedCorrespondence;

    partial void OnSelectedCorrespondenceChanged(CorrespondenceItem? value)
    {
        if (value is null) return;
        FireAndForget.Run(() => ShowCorrespondenceAsync(value), "加载往来邮件");
        SelectedCorrespondence = null;
    }

    [RelayCommand]
    private void ToggleContextSidebar()
    {
        IsContextSidebarVisible = !IsContextSidebarVisible;
        _settings.Current.ShowContextSidebar = IsContextSidebarVisible;
        _ = _settings.SaveAsync();
    }

    // ---------------- 左侧栏分区折叠（快捷视图 / 标签） ----------------

    /// <summary>「快捷视图」分区是否展开；折叠后只留标题条，状态记进设置。</summary>
    [ObservableProperty]
    private bool _isQuickViewsExpanded = true;

    /// <summary>「标签」分区是否展开；折叠后只留标题条，状态记进设置。</summary>
    [ObservableProperty]
    private bool _isTagsExpanded = true;

    [RelayCommand]
    private void ToggleQuickViews()
    {
        IsQuickViewsExpanded = !IsQuickViewsExpanded;
        _settings.Current.SidebarQuickViewsExpanded = IsQuickViewsExpanded;
        _ = _settings.SaveAsync();
    }

    [RelayCommand]
    private void ToggleTags()
    {
        IsTagsExpanded = !IsTagsExpanded;
        _settings.Current.SidebarTagsExpanded = IsTagsExpanded;
        _ = _settings.SaveAsync();
    }

    public bool HasDetailTags => DetailTags.Count > 0;

    partial void OnDetailTagsChanged(IReadOnlyList<MailTag> value) => OnPropertyChanged(nameof(HasDetailTags));

    // ---------------- 货代业务标识（一票货聚合） ----------------

    /// <summary>当前邮件抽取出的业务单号（Job / SO / B/L / 箱号 …），点击可在全库按单号聚合。</summary>
    [ObservableProperty]
    private IReadOnlyList<FreightReference> _detailReferences = Array.Empty<FreightReference>();

    public bool HasDetailReferences => FreightFlags.References && DetailReferences.Count > 0;

    partial void OnDetailReferencesChanged(IReadOnlyList<FreightReference> value)
        => OnPropertyChanged(nameof(HasDetailReferences));

    /// <summary>侧栏「业务单号」：按最近往来排序的一票货入口。</summary>
    public ObservableCollection<FreightReferenceSummary> BusinessRefs { get; } = new();

    [ObservableProperty]
    private string _businessRefsSummary = "";

    // ---------------- 货代时效节点（截关 / 补料 / VGM / ETD / ETA） ----------------

    /// <summary>侧栏时效看板：即将到期与刚刚超时的节点，按到期时间升序。</summary>
    public ObservableCollection<DeadlineBoardItem> Deadlines { get; } = new();

    [ObservableProperty]
    private string _deadlineSummary = "";

    /// <summary>当前邮件抽出的时效节点（详情卡使用）。</summary>
    [ObservableProperty]
    private IReadOnlyList<DeadlineBoardItem> _detailDeadlines = Array.Empty<DeadlineBoardItem>();

    public bool HasDetailDeadlines => FreightFlags.Deadlines && DetailDeadlines.Count > 0;

    partial void OnDetailDeadlinesChanged(IReadOnlyList<DeadlineBoardItem> value)
        => OnPropertyChanged(nameof(HasDetailDeadlines));

    /// <summary>当前邮件所属的一票货案件 id；0 表示未归入案件。</summary>
    [ObservableProperty]
    private long _detailCaseId;

    public bool HasDetailCase => DetailCaseId > 0;

    partial void OnDetailCaseIdChanged(long value) => OnPropertyChanged(nameof(HasDetailCase));

    /// <summary>手动刷新时效看板（先补扫未索引的邮件，再重新计算倒计时）。</summary>
    [RelayCommand]
    private async Task RefreshDeadlinesAsync()
    {
        try
        {
            await Task.Run(() => _referenceService.IndexPendingAsync(500, 4));
            await LoadBusinessRefsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "补扫业务标识失败");
        }

        await LoadDeadlinesAsync();
        StatusText = Deadlines.Count == 0
            ? "暂无即将到期的截关 / 开船 / 到港节点"
            : $"发现 {Deadlines.Count} 个时效节点";
    }

    /// <summary>打开一票货案件页（时间轴 + 节点 + 附件 + 资料包导出）。</summary>
    [RelayCommand]
    private async Task OpenCaseAsync(long caseId)
    {
        if (caseId <= 0) return;

        try
        {
            _dialogs.ShowShipmentCase(caseId, text => _ = ApplyReferenceSearchAsync(text, text));
        }
        finally
        {
            // 案件页里可能关闭了案件，回到主窗口要立刻反映到看板
            await LoadDeadlinesAsync();
        }
    }

    /// <summary>打开当前邮件所属案件；未归入案件时给出提示。</summary>
    [RelayCommand]
    private Task OpenDetailCaseAsync()
    {
        if (DetailCaseId <= 0)
        {
            StatusText = "这封邮件还没有归入一票货（未识别到 SO / 提单号 / 箱号）";
            return Task.CompletedTask;
        }

        return OpenCaseAsync(DetailCaseId);
    }

    private async Task LoadDeadlinesAsync()
    {
        // 设置里关掉时效节点后不再查库、也不显示看板
        if (!FreightFlags.Deadlines)
        {
            Deadlines.Clear();
            DeadlineSummary = "";
            return;
        }

        try
        {
            var items = await _shipments.GetUpcomingDeadlinesAsync(8);
            Deadlines.Clear();
            foreach (var item in items) Deadlines.Add(item);
            DeadlineSummary = items.Count == 0 ? "暂无近期节点" : $"{items.Count} 个节点";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "加载时效看板失败");
        }
    }

    // ---------------- 等待回复（FollowUp） ----------------

    /// <summary>进行中的等待回复数量（工具栏按钮上的角标）。</summary>
    [ObservableProperty]
    private int _activeFollowUpCount;

    public string FollowUpButtonText => ActiveFollowUpCount > 0 ? $"等待回复 ({ActiveFollowUpCount})" : "等待回复";

    partial void OnActiveFollowUpCountChanged(int value) => OnPropertyChanged(nameof(FollowUpButtonText));

    private async Task LoadFollowUpCountAsync()
    {
        try
        {
            ActiveFollowUpCount = await _followUpService.CountActiveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取等待回复数量失败");
        }
    }

    /// <summary>为当前邮件登记「等待回复」：勾选要等谁回、什么时候检查。</summary>
    [RelayCommand]
    private async Task CreateFollowUpAsync()
    {
        var detail = Detail;
        if (detail is null && SelectedRow is { } row)
        {
            detail = await _messages.GetDetailAsync(row.Id);
        }

        if (detail is null)
        {
            StatusText = "请先选择一封邮件";
            return;
        }

        var self = Accounts.FirstOrDefault(account => account.Id == detail.AccountId)?.Model.Email ?? string.Empty;
        var request = _dialogs.ShowFollowUpEditor(detail, self);
        if (request is null) return;

        var (ok, error, _) = await _followUpService.CreateAsync(
            detail,
            request.Recipients,
            request.RequireAll,
            request.MinReplies,
            request.IncludeCc,
            request.NextCheckUtc,
            request.Note,
            selfAddress: self);

        StatusText = ok
            ? $"已开始跟踪等待回复（{request.Recipients.Count} 位联系人，{request.NextCheckUtc.ToLocalTime():MM-dd HH:mm} 检查）"
            : error ?? "创建等待回复失败";
        await LoadFollowUpCountAsync();
    }

    /// <summary>打开「等待回复」管理器；用户在里面可能要求打开邮件或写催信。</summary>
    [RelayCommand]
    private async Task OpenFollowUpsAsync()
    {
        var request = _dialogs.ShowFollowUps();
        await LoadFollowUpCountAsync();

        if (request is null) return;

        if (request.Kind == FollowUpActionKind.OpenMessage)
        {
            await OpenMessageByIdAsync(request.FollowUp.EmailId);
            return;
        }

        await ComposeFollowUpReminderAsync(request);
    }

    /// <summary>精确定位一封邮件：列表只显示这一封，避免在分页里翻找。</summary>
    private async Task OpenMessageByIdAsync(long emailId)
    {
        if (emailId <= 0) return;

        SearchText = string.Empty;
        _pinnedEmailId = emailId;
        IsSearchResults = false;
        IsSearchScopeAllMail = false;
        CurrentPage = 1;
        ListTitle = "定位邮件";
        await ReloadMessagesAsync();

        if (Messages.Count == 0)
        {
            _pinnedEmailId = null;
            StatusText = "这封邮件已不存在（可能已被清理）";
            await ReloadMessagesAsync();
        }
    }

    /// <summary>给未回复的人写催信：只带未回复的地址，正文沿用原邮件引用。</summary>
    private async Task ComposeFollowUpReminderAsync(FollowUpActionRequest request)
    {
        var detail = await _messages.GetDetailAsync(request.FollowUp.EmailId);
        if (detail is null)
        {
            StatusText = "原邮件已不存在，无法撰写催信";
            return;
        }

        if (request.Pending.Count == 0)
        {
            StatusText = "对方已全部回复，无需催信";
            return;
        }

        _dialogs.ShowCompose(new ComposeRequest
        {
            Mode = ComposeMode.Reply,
            AccountId = detail.AccountId,
            To = string.Join("; ", request.Pending),
            Subject = ComposeRequestFactory.SubjectPrefix(detail.Subject, "Re:"),
            // 催信同样是 HTML：原文的排版与图片一起带过去，和手动回复保持一致
            Body = HtmlTextExtractor.ToHtml("跟进提醒：关于下面这封邮件，尚未收到您的回复，烦请确认，谢谢。")
                + ComposeRequestFactory.BuildQuoteHtml(
                    detail,
                    path => ComposeRequestFactory.ReadLocalImage(path, _emlDirectory)),
            InReplyTo = detail.MessageId,
            References = ComposeRequestFactory.JoinReferences(detail)
        });
    }

    [RelayCommand]
    private Task SelectBusinessRefAsync(FreightReferenceSummary? reference)
        => reference is null
            ? Task.CompletedTask
            : ApplyReferenceSearchAsync(reference.SearchText, $"{reference.KindLabel} · {reference.Value}");

    [RelayCommand]
    private Task FilterByReferenceAsync(FreightReference? reference)
        => reference is null
            ? Task.CompletedTask
            : ApplyReferenceSearchAsync(reference.SearchText, reference.Display);

    /// <summary>按业务单号跨文件夹、跨账户聚合邮件（列表标题改为单号）。</summary>
    private Task ApplyReferenceSearchAsync(string searchText, string title)
    {
        foreach (var quick in QuickViews) quick.IsSelected = false;
        foreach (var account in Accounts)
        {
            foreach (var folder in account.Folders) folder.IsSelected = false;
        }

        _currentQuickView = null;
        _currentFolder = null;
        _currentTag = null;
        _pinnedEmailId = null;
        SearchText = searchText;

        _suppressSearchReload = true;
        try
        {
            IsSearchScopeAllMail = true;
            IsSearchResults = true;
        }
        finally
        {
            _suppressSearchReload = false;
        }

        CurrentPage = 1;
        ListTitle = title;
        return ReloadMessagesAsync();
    }

    /// <summary>手动重扫业务单号（历史上首次升级后需要跑一次全量补扫）。</summary>
    [RelayCommand]
    private async Task RefreshBusinessRefsAsync()
    {
        StatusText = "正在扫描业务单号…";
        try
        {
            var indexed = await Task.Run(() => _referenceService.IndexPendingAsync(500, 20));
            await LoadBusinessRefsAsync();
            await LoadDeadlinesAsync();
            StatusText = $"已扫描 {indexed} 封邮件，识别 {BusinessRefs.Count} 个业务单号";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扫描业务单号失败");
            StatusText = "扫描业务单号失败：" + ex.Message;
        }
    }

    private async Task LoadBusinessRefsAsync()
    {
        // 设置里关掉业务编号后不再查库、也不显示聚合入口
        if (!FreightFlags.References)
        {
            BusinessRefs.Clear();
            BusinessRefsSummary = "";
            return;
        }

        var top = await _referenceService.GetTopAsync(null, 10);
        BusinessRefs.Clear();
        foreach (var item in top) BusinessRefs.Add(item);

        var pending = await _referenceService.CountPendingAsync();
        BusinessRefsSummary = pending > 0 ? $"待补扫 {pending} 封" : "按最近往来排序";
    }

    /// <summary>后台补扫历史邮件并刷新侧栏；失败只记日志，不影响主流程。</summary>
    private async Task WarmReferenceIndexAsync()
    {
        try
        {
            var indexed = await Task.Run(() => _referenceService.IndexPendingAsync(500, 40));
            if (indexed > 0) _logger.LogInformation("业务标识补扫完成：{Count} 封", indexed);
            await LoadBusinessRefsAsync();
            await LoadDeadlinesAsync();
            await LoadFollowUpCountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "业务标识补扫失败");
        }
    }

    [ObservableProperty]
    private string _listTitle = "统一收件箱";

    [ObservableProperty]
    private string _listSubtitle = "";

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>当前列表是否处于搜索结果模式；选择文件夹 / 快捷视图会退出，但保留搜索框文字。</summary>
    [ObservableProperty]
    private bool _isSearchResults;

    /// <summary>搜索结果范围：false=当前选中文件夹 / 视图，true=所有邮件。</summary>
    [ObservableProperty]
    private bool _isSearchScopeAllMail;

    public bool IsSearchScopeCurrentView
    {
        get => !IsSearchScopeAllMail;
        set { if (value) IsSearchScopeAllMail = false; }
    }

    /// <summary>搜索条件 chips 只在搜索结果模式下展示，避免文件夹列表出现“假筛选”。</summary>
    public bool ShowSearchChips => IsSearchResults && HasSearchChips;

    [ObservableProperty]
    private MailListFilter _filter = MailListFilter.All;

    /// <summary>阅读窗格是否正在加载（读取详情 / 下载正文）。启动时先显示加载态，避免右侧空白。</summary>
    [ObservableProperty]
    private bool _isDetailLoading = true;

    /// <summary>账户与文件夹是否正在首次加载；侧栏用它显示轻量加载行。</summary>
    [ObservableProperty]
    private bool _isAccountsLoading = true;

    /// <summary>邮件列表是否正在首次加载；列表区用它显示加载占位。</summary>
    [ObservableProperty]
    private bool _isListLoading = true;

    [ObservableProperty]
    private bool _conversationMode;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private bool _isDark;

    [ObservableProperty]
    private string _statusText = "正在加载…";

    [ObservableProperty]
    private double _syncPercent;

    [ObservableProperty]
    private bool _isSyncIndeterminate = true;

    [ObservableProperty]
    private int _localTotal;

    /// <summary>本地未读邮件总数（状态栏展示）。</summary>
    [ObservableProperty]
    private int _localUnread;

    /// <summary>尚未同步到服务器的本地写操作数量。</summary>
    [ObservableProperty]
    private int _pendingSyncCount;

    /// <summary>最近一次删除是否可撤销（仅在服务器尚未确认时）。</summary>
    [ObservableProperty]
    private bool _canUndoDelete;

    private long _lastDeleteOperationId;

    [ObservableProperty]
    private bool _hasMore;

    /// <summary>当前页（1 起）。列表按「每页封数」分页加载，避免一次性拉取过多。</summary>
    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalCount;

    private int PageSize => Math.Clamp(_settings.Current.ListPageSize, 10, 500);

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public bool CanGoPrevious => CurrentPage > 1;

    public bool CanGoNext => CurrentPage < TotalPages;

    public string PageInfo => $"第 {CurrentPage} / {TotalPages} 页";

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageInfo));
    }

    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(ShowPagination));
    }

    [ObservableProperty]
    private bool _isEmpty;

    public string AccountSummary => $"已连接 {Accounts.Count} 个账户 · IMAP 增量同步";

    /// <summary>标题栏展示的版本标识（v1.2.0）：与「关于」页共用同一个来源。</summary>
    public string AppVersion => AppVersionInfo.DisplayWithPrefix;

    public bool HasDetail => Detail is not null;

    public bool HasSelection => SelectedRow is not null;

    [ObservableProperty]
    private int _batchSelectedCount;

    /// <summary>当前页已选邮件，供批量处置条与快捷键使用。</summary>
    public bool HasBatchSelection => BatchSelectedCount > 0;

    public string BatchSelectionText => $"已选 {BatchSelectedCount} 封";

    /// <summary>批量处置条只在多选时出现：单选的操作在阅读工具栏里已有，不必占分页那一行。</summary>
    public bool ShowBatchBar => BatchSelectedCount > 1;

    public bool ShowPagination => BatchSelectedCount <= 1 && TotalCount > 0;

    partial void OnBatchSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasBatchSelection));
        OnPropertyChanged(nameof(BatchSelectionText));
        OnPropertyChanged(nameof(ShowBatchBar));
        OnPropertyChanged(nameof(ShowPagination));
    }

    /// <summary>当前选中邮件的已读状态切换文案。</summary>
    public string ReadToggleText => SelectedRow is { IsUnread: true } ? "标为已读" : "标为未读";

    /// <summary>当前选中邮件是否已加星标：工具条上的星标按钮据此换成实心高亮。</summary>
    public bool IsSelectedStarred => SelectedRow is { IsStarred: true };

    /// <summary>当前选中邮件是否未读：工具条据此在「标为已读（对勾）」与「标为未读（信封）」之间换图标。</summary>
    public bool IsSelectedUnread => SelectedRow is { IsUnread: true };

    /// <summary>是否存在待同步操作（状态栏提示）。</summary>
    public bool HasPendingSync => PendingSyncCount > 0;

    public string PendingSyncText => $"待同步 {PendingSyncCount} 项";

    partial void OnPendingSyncCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPendingSync));
        OnPropertyChanged(nameof(PendingSyncText));
    }

    /// <summary>附件区展示的普通附件；内嵌图片属于正文资源，不单独列出。</summary>
    public IReadOnlyList<AttachmentInfo> VisibleAttachments =>
        Detail?.Attachments.Where(a => !a.IsInline).ToList() ?? new List<AttachmentInfo>();

    public bool HasAttachments => VisibleAttachments.Count > 0;

    /// <summary>纯文本回退时可供富文本框内联展示的图片路径（正文内嵌资源）。</summary>
    public IReadOnlyList<string> InlineImagePaths =>
        Detail?.Attachments
            .Where(a => a.IsInline && a.IsDownloaded && !string.IsNullOrEmpty(a.LocalPath))
            .Select(a => a.LocalPath!)
            .ToList() ?? new List<string>();

    /// <summary>详情尚未到达时的居中加载态（只覆盖本地读邮件头那一瞬间）。</summary>
    public bool ShowDetailLoading => Detail is null && IsDetailLoading;

    /// <summary>未选中任何邮件时的空态占位。</summary>
    public bool ShowDetailPlaceholder => Detail is null && !IsDetailLoading;

    /// <summary>正文仍在按需下载（邮件头已展示，正文区显示加载态）。</summary>
    [ObservableProperty]
    private bool _isBodyLoading;

    /// <summary>正文区域是否显示正文：下载中先让位给加载占位，避免一大片空白。</summary>
    public bool ShowBodyView => !IsBodyLoading;

    /// <summary>
    /// 正文控件（含 WebView2）是否可见。
    /// WebView2 是独立 HWND，永远盖在同一层 Grid 里的 WPF 元素之上——只要它还在渲染，
    /// 叠在上面的「重新获取正文」卡片就看不见。所以需要卡片时把正文控件整块收起来。
    /// </summary>
    public bool ShowBodyContent => ShowBodyView && !ShowReloadBodyButton;

    partial void OnIsBodyLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBodyView));
        OnPropertyChanged(nameof(ShowReloadBodyButton));
        OnPropertyChanged(nameof(ShowBodyContent));
    }

    /// <summary>
    /// 正文取不到时给一个「重新获取正文」入口：正文为空但状态已是「已下载」的邮件
    /// （上次解析失败 / 正文缓存被清 / 服务器返回空体）不会再自动重拉，只能由用户手动触发。
    /// 草稿不出现（正文是本地内容），规则见 <see cref="MailBodyRecovery"/>。
    /// </summary>
    public bool ShowReloadBodyButton => MailBodyRecovery.ShouldOfferReload(Detail, IsBodyLoading);

    /// <summary>重新拉取当前邮件的正文（忽略本地「已下载」标记），成功后就地刷新阅读区。</summary>
    [RelayCommand]
    private async Task ReloadBodyAsync()
    {
        if (Detail is not { } header || IsBodyLoading) return;

        var version = _detailLoader.Begin();

        var previous = Interlocked.Exchange(ref _detailCts, null);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _detailCts = cts;
        var ct = cts.Token;

        IsBodyLoading = true;
        StatusText = "正在重新获取正文…";
        try
        {
            var refreshed = await _detailLoader.ReloadBodyAsync(header, version, text => StatusText = text, ct);
            if (refreshed is null || !_detailLoader.IsCurrent(version)) return;

            if (!ReferenceEquals(refreshed, header)) Detail = refreshed;
            StatusText = "就绪";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户切走了 / 关了窗口：静默收尾
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重新获取邮件 {Id} 正文失败", header.Id);
            StatusText = "重新获取正文失败：" + ex.Message;
        }
        finally
        {
            if (_detailLoader.IsCurrent(version)) IsBodyLoading = false;
        }
    }

    /// <summary>邮件头展示的原始大小；未知或零字节时显示占位而不是误导性的 0 B。</summary>
    public string SizeDisplayText => Detail is { Size: > 0 } detail
        ? DisplayFormat.FileSize(detail.Size)
        : "未知";

    public string BodyDisplay => Detail is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(Detail.BodyText) ? "（此邮件暂无正文内容）" : Detail.BodyText;

    /// <summary>阅读窗格正文字号，跟随设置页的「紧凑 / 标准 / 较大」。</summary>
    public double BodyFontSize => _settings.Current.FontSize switch
    {
        "compact" => 12.5,
        "large" => 15.5,
        _ => 13.5
    };

    // ---------------- 正文渲染 ----------------

    /// <summary>是否用 HTML 渲染正文（设置页开关；关闭后回退纯文本）。</summary>
    [ObservableProperty]
    private bool _useHtmlRendering;

    /// <summary>正文 HTML 文档：主题、字号、远程图片开关与附件目录都参与组装（缓存 + 后台组装）。</summary>
    public string BodyHtmlDocument => _bodyDocument.Document;

    /// <summary>原始正文 HTML，供纯文本回退位面转换为富文本。</summary>
    public string BodyHtmlSource => Detail?.BodyHtml ?? string.Empty;

    /// <summary>内联附件目录（正文控件映射为虚拟主机）。</summary>
    public string EmlDirectory => _emlDirectory;

    /// <summary>正文含远程图片且尚未放行时，显示「显示图片」提示条。</summary>
    public bool ShowRemoteImagesPrompt => UseHtmlRendering
        && Detail is not null
        && !_loadRemoteImages
        && _settings.Current.BlockRemoteImages
        && _bodyDocument.HasRemoteImages;

    /// <summary>
    /// 重算正文文档：小正文就地组装（几毫秒），大正文交给线程池，组装好了再通知界面。
    /// 以前这里是绑定 getter 里的同步组装，8MB 的邮件一开就占界面线程 200–300ms。
    /// </summary>
    private void RefreshBodyDocument()
    {
        if (!UseHtmlRendering || Detail is not { } detail)
        {
            _bodyDocument.Reset();
            OnPropertyChanged(nameof(BodyHtmlDocument));
            OnPropertyChanged(nameof(ShowRemoteImagesPrompt));
            return;
        }

        var options = CreateBodyDocumentOptions();
        var key = MailBodyDocumentFactory.BuildKey(detail, options);

        if (detail.BodyHtml is null || detail.BodyHtml.Length <= MailBodyDocumentCache.InlineBuildMaxChars)
        {
            if (_bodyDocument.TryBuildInline(key, () => MailBodyAuditLog.Build(detail, options)))
            {
                OnPropertyChanged(nameof(BodyHtmlDocument));
                OnPropertyChanged(nameof(ShowRemoteImagesPrompt));
            }

            return;
        }

        _ = BuildBodyDocumentAsync(detail, options, key);
    }

    private async Task BuildBodyDocumentAsync(EmailDetail detail, MailDocumentOptions options, string key)
    {
        // 结果回到界面线程后才赋值；被更新的请求取代时 BuildAsync 返回 false，不刷新界面
        if (!await _bodyDocument.BuildAsync(key, () => MailBodyAuditLog.Build(detail, options)).ConfigureAwait(true))
            return;

        OnPropertyChanged(nameof(BodyHtmlDocument));
        OnPropertyChanged(nameof(ShowRemoteImagesPrompt));
    }

    private MailDocumentOptions CreateBodyDocumentOptions() => MailBodyDocumentFactory.CreateOptions(
        IsDark,
        BodyFontSize,
        _loadRemoteImages || !_settings.Current.BlockRemoteImages,
        _settings.Current.FoldQuotedHistory,
        _settings.Current.FoldQuotedHistoryKeepLayers,
        _emlDirectory);

    partial void OnUseHtmlRenderingChanged(bool value) => RefreshBodyDocument();

    partial void OnIsDarkChanged(bool value) => RefreshBodyDocument();

    /// <summary>同步期间账户右键里的「收取」要跟着置灰，避免重复点；刷新按钮的说明也换成同步中。</summary>
    partial void OnIsSyncingChanged(bool value)
    {
        SyncAccountCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RefreshToolTip));
    }

    /// <summary>刷新按钮的悬停说明：同步中给出明确提示，避免用户以为刚才那下没生效。</summary>
    public string RefreshToolTip => IsSyncing ? "正在同步…（同步完成后列表会自动刷新）" : "刷新 / 增量同步（Ctrl+R 或 F5）";

    /// <summary>上下文侧边栏开关的说明：直接写明当前是开还是关。</summary>
    public string ContextSidebarToggleHint => IsContextSidebarVisible
        ? "上下文侧边栏：已打开（点击隐藏）"
        : "上下文侧边栏：已关闭（点击显示）";

    partial void OnIsContextSidebarVisibleChanged(bool value)
        => OnPropertyChanged(nameof(ContextSidebarToggleHint));

    /// <summary>本封邮件放行远程图片（仅本次阅读，不写入设置）。</summary>
    [RelayCommand]
    private void LoadRemoteImages()
    {
        if (_loadRemoteImages) return;

        _loadRemoteImages = true;
        RefreshBodyDocument();
        StatusText = "已加载远程图片";
    }

    // ---------------- 存储空间监控 ----------------

    private DispatcherTimer? _storageTimer;

    [ObservableProperty]
    private StorageSnapshot? _storage;

    /// <summary>是否触发分级空间不足预警（75% / 85% / 95%）。</summary>
    public bool HasStorageAlert => Storage is { HasAlert: true } && _settings.Current.StorageAlertsEnabled;

    /// <summary>状态栏常驻的本地占用摘要。</summary>
    public string StorageStatus => Storage is null
        ? "本地占用统计中…"
        : $"本地占用 {Storage.TotalDisplay} · 可用 {Storage.FreeDisplay}";

    public string StorageAlertText => Storage?.AlertText ?? "";

    public string StorageReclaimableText => Storage is { ReclaimableBytes: > 0 } snapshot
        ? $"可释放约 {snapshot.ReclaimableDisplay}"
        : "";

    partial void OnStorageChanged(StorageSnapshot? value)
    {
        OnPropertyChanged(nameof(HasStorageAlert));
        OnPropertyChanged(nameof(StorageStatus));
        OnPropertyChanged(nameof(StorageAlertText));
        OnPropertyChanged(nameof(StorageReclaimableText));
    }

    /// <summary>上一次存储快照与其时间：3 分钟定时器与清理窗口关闭后的刷新共用，避免重复扫目录。</summary>
    private StorageSnapshot? _storageCache;
    private DateTime _storageCacheAtUtc;

    /// <summary>刷新存储快照。聚合 SQL + 目录扫描必须在线程池，界面线程只做赋值。</summary>
    public async Task RefreshStorageAsync(bool force = false)
    {
        if (!force
            && _storageCache is { } cached
            && DateTime.UtcNow - _storageCacheAtUtc < TimeSpan.FromMinutes(1))
        {
            Storage = cached;
            return;
        }

        try
        {
            var snapshot = await Task.Run(() => _storageMonitor.GetSnapshotAsync()).ConfigureAwait(true);
            _storageCache = snapshot;
            _storageCacheAtUtc = DateTime.UtcNow;
            Storage = snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取本地存储快照失败");
        }
    }

    /// <summary>后台每 3 分钟刷新一次占用，配合分级阈值实现准实时预警。</summary>
    private void StartStorageWatch()
    {
        if (_storageTimer is not null) return;

        _storageTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        _storageTimer.Tick += async (_, _) => await RefreshStorageAsync();
        _storageTimer.Start();
    }

    /// <summary>打开历史邮件清理窗口，关闭后立即刷新占用。</summary>
    [RelayCommand]
    private async Task OpenCleanupAsync()
    {
        _dialogs.ShowCleanup();
        await RefreshStorageAsync(force: true);
    }

    /// <summary>从账户节点右键进入清理：作用域预置为该账户。</summary>
    [RelayCommand]
    private async Task OpenCleanupForAccountAsync(AccountNode? node)
    {
        if (node is null) return;

        _dialogs.ShowCleanup(new CleanupScope(AccountId: node.Id, FolderId: null, Label: $"账户「{node.Title}」"));
        await RefreshStorageAsync(force: true);
    }

    /// <summary>从文件夹节点右键进入清理：作用域预置为该文件夹。</summary>
    [RelayCommand]
    private async Task OpenCleanupForFolderAsync(FolderNode? folder)
    {
        if (folder is null) return;

        _dialogs.ShowCleanup(new CleanupScope(AccountId: folder.AccountId, FolderId: folder.Model.Id, Label: $"文件夹「{folder.Title}」"));
        await RefreshStorageAsync(force: true);
    }

    // ---------------- 初始化 ----------------

    /// <summary>加载设置与本地数据，并默认选中「统一收件箱」。</summary>
    public async Task InitializeAsync()
    {
        IsAccountsLoading = true;
        IsListLoading = true;
        IsDetailLoading = true;
        StatusText = "正在加载账户与文件夹…";

        IsDark = _theme.IsDark;
        UseHtmlRendering = _settings.Current.HtmlRendering;

        // 「全部搜索条件」面板显示哪些行（用户的勾选记在设置里）
        LoadSearchFilterVisibility();

        _suspendReload = true;
        ConversationMode = _settings.Current.ConversationDefault;
        _suspendReload = false;

        // 让主窗口先画出标题栏、工具栏与加载占位，再开始读本地库。
        await LetUiRenderAsync();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var stages = new List<string>();
        void Mark(string stage)
        {
            stages.Add($"{stage} {clock.ElapsedMilliseconds}ms");
            // 每段各落一行：初始化卡住时，日志最后一行就是卡住的位置
            StartupTrace.Mark("界面 " + stage);
        }

        try
        {
            await ReloadAccountsAsync();
            Mark("账户与文件夹");
            IsAccountsLoading = false;
            StatusText = "正在加载邮件列表…";
            await LetUiRenderAsync();

            // 列表首次上屏后即可交互；标签、统计、存储占用等放在后面补。
            await SelectQuickViewAsync(QuickViews[0]);
            Mark("列表首屏");
            IsListLoading = false;
            StatusText = "正在统计本地邮件…";
            await LetUiRenderAsync();

            await ReloadSidebarTagsAsync();
            await RefreshLocalTotalAsync();
            Mark("标签与统计");
            // 存储快照要递归统计整个数据目录（机械盘上实测 60 秒以上），不能挡启动：
            // 放到「就绪」之后后台补，状态栏的数字晚几秒出现，但界面立刻可用。
            FireAndForget.Run(() => RefreshStorageAsync(force: true), "刷新存储占用");
            await _mailActions.InitializeAsync();
            await RefreshPendingStateAsync();
            Mark("待同步队列");
            StartStorageWatch();
            StatusText = "就绪";
            Mark("就绪");

            // 首屏各段耗时：一行进日志，定位「启动慢」时不用猜
            _logger.LogInformation("启动计时（界面初始化）：{Stages}", string.Join(" · ", stages));

            // 后台补扫业务单号（首次升级需要处理历史邮件），不阻塞首屏
            FireAndForget.Run(WarmReferenceIndexAsync, "预建业务单号索引");

            // 设置文件损坏时已回退默认值并备份原文件，这里给出轻量提示（含备份文件名）
            if (_settings.LoadWarning is { Length: > 0 } warning) StatusText = warning;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "主界面初始化失败");
            StatusText = "加载主界面失败：" + ex.Message;
            IsEmpty = Messages.Count == 0;
            IsDetailLoading = false;
        }
        finally
        {
            // 任何失败都不能让加载占位一直盖住界面。
            IsAccountsLoading = false;
            IsListLoading = false;
        }
    }

    /// <summary>
    /// 让 WPF 先完成一次布局与绘制，再继续后续数据库读取。
    /// 测试环境没有 Application / Dispatcher，直接返回，避免 await 卡住测试线程。
    /// </summary>
    private static async Task LetUiRenderAsync()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || !dispatcher.CheckAccess()) return;

        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
    }

    private void BuildQuickViews()
    {
        QuickViews.Add(new QuickViewItem("统一收件箱", "IcoInbox", MailListFilter.All, FolderKind.Inbox));
        QuickViews.Add(new QuickViewItem("未读邮件", "IcoMail", MailListFilter.Unread));
        QuickViews.Add(new QuickViewItem("星标邮件", "IcoStar", MailListFilter.Starred));
        QuickViews.Add(new QuickViewItem("含附件", "IcoPaperclip", MailListFilter.Attachments));
        QuickViews.Add(new QuickViewItem("草稿箱", "IcoDraft", MailListFilter.All, FolderKind.Drafts));
    }

    // ---------------- 侧栏选择 ----------------

    [RelayCommand]
    private Task SelectQuickViewAsync(QuickViewItem? item)
    {
        if (item is null) return Task.CompletedTask;

        foreach (var quick in QuickViews) quick.IsSelected = ReferenceEquals(quick, item);
        foreach (var account in Accounts)
        {
            foreach (var folder in account.Folders) folder.IsSelected = false;
        }

        _currentQuickView = item;
        _currentFolder = null;
        _currentTag = null;
        _pinnedEmailId = null;
        IsSearchResults = false;
        IsSearchScopeAllMail = false;
        CurrentPage = 1;
        ListTitle = item.Title;

        _suspendReload = true;
        Filter = item.Filter;
        _suspendReload = false;

        RefreshSidebarTagsForContext();
        return ReloadMessagesAsync();
    }

    [RelayCommand]
    private Task SelectFolderAsync(FolderNode? folder)
    {
        if (folder is null) return Task.CompletedTask;

        foreach (var quick in QuickViews) quick.IsSelected = false;
        foreach (var account in Accounts)
        {
            foreach (var node in account.Folders) node.IsSelected = ReferenceEquals(node, folder);
        }

        _currentFolder = folder;
        _currentQuickView = null;
        _currentTag = null;
        _pinnedEmailId = null;
        IsSearchResults = false;
        IsSearchScopeAllMail = false;
        CurrentPage = 1;
        ListTitle = folder.Title;

        RefreshSidebarTagsForContext();
        return ReloadMessagesAsync();
    }

    // ---------------- 列表 ----------------

    // ---------------- 搜索条件 chip ----------------

    /// <summary>当前搜索里生效的一个结构化条件；Pattern 用于从搜索串里摘除它。</summary>
    public sealed record SearchChip(string Pattern, string Label);

    /// <summary>联系人分组（收件人 / 抄送 / 密送）。</summary>
    public sealed record MailRecipientGroup(string Label, IReadOnlyList<EmailAddress> Addresses);

    public System.Collections.ObjectModel.ObservableCollection<SearchChip> SearchChips { get; } = new();

    public bool HasSearchChips => SearchChips.Count > 0;

    private static readonly System.Text.RegularExpressions.Regex ChipTokenRegex = new(
        @"[\p{L}\p{N}_/]+:(?:""[^""]*""|[^\s]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>把搜索串里的运算符拆成可移除的 chip；自由文本不生成 chip，避免误解成"整句可删"。</summary>
    private void RefreshSearchChips()
    {
        SearchChips.Clear();
        var text = SearchText ?? string.Empty;

        foreach (System.Text.RegularExpressions.Match match in ChipTokenRegex.Matches(text))
        {
            var token = match.Value;
            var colon = token.IndexOf(':');
            if (colon <= 0) continue;

            var key = token[..colon];
            var value = token[(colon + 1)..].Trim('"');
            var lower = key.ToLowerInvariant();

            var label = lower switch
            {
                "from" or "发件人" => "发件人：" + value,
                "subject" or "主题" => "主题：" + value,
                "filename" or "fname" or "附件名" => "附件：" + value,
                "after" or "since" or "之后" => "之后：" + value,
                "before" or "until" or "之前" => "之前：" + value,
                "has" => "含附件",
                "is" when value.Contains("unread", StringComparison.OrdinalIgnoreCase) || value == "未读" => "未读",
                "is" when value == "starred" || value == "星标" => "星标",
                "type" or "类型" => "类型：" + value,
                _ => key + "：" + value
            };

            SearchChips.Add(new SearchChip(System.Text.RegularExpressions.Regex.Escape(token), label));
        }

        OnPropertyChanged(nameof(HasSearchChips));
        OnPropertyChanged(nameof(ShowSearchChips));
    }

    /// <summary>点掉一个 chip：从搜索串里删掉对应运算符并重新检索。</summary>
    [RelayCommand]
    private async Task RemoveSearchChipAsync(SearchChip? chip)
    {
        if (chip is null) return;

        SearchText = System.Text.RegularExpressions.Regex
            .Replace(SearchText ?? string.Empty, chip.Pattern, string.Empty)
            .Replace("  ", " ")
            .Trim();

        await SearchAsync();
    }

    // ---------------- 全部搜索条件（对齐 Foxmail 的「全部搜索条件」面板） ----------------

    /// <summary>筛选条是否展开（内联在工具栏下方，不是悬浮框，方便从正文复制粘贴）。</summary>
    [ObservableProperty]
    private bool _isSearchFilterOpen;

    partial void OnIsSearchFilterOpenChanged(bool value)
    {
        // 展开时把本地标签补进「标签」下拉（新增 / 改名的标签不用重启就能选到）
        if (value) _ = LoadSearchFilterTagsAsync();
    }

    [ObservableProperty]
    private string _searchFilterFrom = "";

    [ObservableProperty]
    private string _searchFilterTo = "";

    [ObservableProperty]
    private string _searchFilterSubject = "";

    [ObservableProperty]
    private string _searchFilterBody = "";

    [ObservableProperty]
    private string _searchFilterFileName = "";

    /// <summary>附件：0 不限 / 1 有附件 / 2 无附件。</summary>
    [ObservableProperty]
    private int _searchFilterAttachmentIndex;

    /// <summary>标签：0 不限 / 1 有标签 / 2 无标签 / 3 起为具体标签（与 <see cref="SearchFilterTagOptions"/> 对齐）。</summary>
    [ObservableProperty]
    private int _searchFilterTagIndex;

    /// <summary>提醒：0 不限 / 1 有提醒 / 2 无提醒。</summary>
    [ObservableProperty]
    private int _searchFilterReminderIndex;

    [ObservableProperty]
    private string _searchFilterNote = "";

    /// <summary>时间：0 时间不限 / 1 今天 / 2 三天内 / 3 一周内 / 4 一个月内 / 5 一年内 / 6 自定义。</summary>
    [ObservableProperty]
    private int _searchFilterTimeIndex;

    /// <summary>自定义时间范围（仅当时间选「自定义」时生效）。</summary>
    [ObservableProperty]
    private DateTime? _searchFilterDateFrom;

    [ObservableProperty]
    private DateTime? _searchFilterDateTo;

    /// <summary>标签下拉的文案（前两项固定，其后是本地标签名）。</summary>
    public ObservableCollection<string> SearchFilterTagOptions { get; } = new() { "不限", "有标签", "无标签" };

    /// <summary>与 <see cref="SearchFilterTagOptions"/> 的第 3 项起一一对应的标签 id。</summary>
    private readonly List<long> _searchFilterTagIds = new();

    /// <summary>当前生效的结构化筛选（null 表示没有条件）。</summary>
    private MailSearchFilter? _activeSearchFilter;

    // ---------------- 搜索面板：哪些条件显示（对齐 Foxmail 的「+」菜单） ----------------

    /// <summary>「+」菜单里可勾选的条件顺序（与设置里保存的键一一对应）。</summary>
    internal static readonly string[] SearchFilterFieldKeys =
        { "from", "to", "subject", "body", "attachment", "tag", "reminder", "note", "time" };

    [ObservableProperty]
    private bool _showFilterFrom = true;

    [ObservableProperty]
    private bool _showFilterTo = true;

    [ObservableProperty]
    private bool _showFilterSubject = true;

    [ObservableProperty]
    private bool _showFilterBody = true;

    [ObservableProperty]
    private bool _showFilterAttachment = true;

    [ObservableProperty]
    private bool _showFilterTag = true;

    [ObservableProperty]
    private bool _showFilterReminder = true;

    [ObservableProperty]
    private bool _showFilterNote = true;

    [ObservableProperty]
    private bool _showFilterTime = true;

    /// <summary>正在从设置回填可见性：期间不写回，避免把默认值覆盖掉用户的选择。</summary>
    private bool _loadingFilterVisibility;

    private void LoadSearchFilterVisibility()
    {
        var hidden = (_settings.Current.SearchFilterHiddenFields ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _loadingFilterVisibility = true;
        try
        {
            ShowFilterFrom = !hidden.Contains("from");
            ShowFilterTo = !hidden.Contains("to");
            ShowFilterSubject = !hidden.Contains("subject");
            ShowFilterBody = !hidden.Contains("body");
            ShowFilterAttachment = !hidden.Contains("attachment");
            ShowFilterTag = !hidden.Contains("tag");
            ShowFilterReminder = !hidden.Contains("reminder");
            ShowFilterNote = !hidden.Contains("note");
            ShowFilterTime = !hidden.Contains("time");
        }
        finally
        {
            _loadingFilterVisibility = false;
        }
    }

    /// <summary>把「哪些条件显示」写回设置（下次打开面板保持同样的面板）。</summary>
    private void PersistSearchFilterVisibility()
    {
        if (_loadingFilterVisibility) return;

        var visible = new[]
        {
            (Key: "from", Show: ShowFilterFrom),
            (Key: "to", Show: ShowFilterTo),
            (Key: "subject", Show: ShowFilterSubject),
            (Key: "body", Show: ShowFilterBody),
            (Key: "attachment", Show: ShowFilterAttachment),
            (Key: "tag", Show: ShowFilterTag),
            (Key: "reminder", Show: ShowFilterReminder),
            (Key: "note", Show: ShowFilterNote),
            (Key: "time", Show: ShowFilterTime)
        };

        _settings.Current.SearchFilterHiddenFields =
            string.Join(',', visible.Where(item => !item.Show).Select(item => item.Key));
        _ = _settings.SaveAsync();
    }

    partial void OnShowFilterFromChanged(bool value) => PersistSearchFilterVisibility();
    partial void OnShowFilterToChanged(bool value) => PersistSearchFilterVisibility();
    partial void OnShowFilterSubjectChanged(bool value) => PersistSearchFilterVisibility();
    partial void OnShowFilterBodyChanged(bool value) => PersistSearchFilterVisibility();
    partial void OnShowFilterAttachmentChanged(bool value) => PersistSearchFilterVisibility();
    partial void OnShowFilterTagChanged(bool value) => PersistSearchFilterVisibility();
    partial void OnShowFilterReminderChanged(bool value) => PersistSearchFilterVisibility();
    partial void OnShowFilterNoteChanged(bool value) => PersistSearchFilterVisibility();
    partial void OnShowFilterTimeChanged(bool value) => PersistSearchFilterVisibility();

    public bool IsSearchFilterCustomRange => SearchFilterTimeIndex == 6;

    partial void OnSearchFilterTimeIndexChanged(int value)
        => OnPropertyChanged(nameof(IsSearchFilterCustomRange));

    /// <summary>把筛选面板的当前取值整理成查询条件（不拼字符串，直接进 <see cref="MessageQuery"/>）。</summary>
    private MailSearchFilter BuildSearchFilter()
    {
        var (since, before) = SearchFilterTimeIndex switch
        {
            1 => (StartOfToday(), (DateTimeOffset?)null),
            2 => (StartOfToday().AddDays(-2), null),
            3 => (StartOfToday().AddDays(-6), null),
            4 => (StartOfToday().AddMonths(-1), null),
            5 => (StartOfToday().AddYears(-1), null),
            6 => (
                SearchFilterDateFrom is { } from ? new DateTimeOffset(from.Date, TimeSpan.Zero).ToLocalTime() : null,
                SearchFilterDateTo is { } to ? new DateTimeOffset(to.Date, TimeSpan.Zero).ToLocalTime().AddDays(1) : null),
            _ => ((DateTimeOffset?)null, (DateTimeOffset?)null)
        };

        var tagId = SearchFilterTagIndex >= 3 && SearchFilterTagIndex - 3 < _searchFilterTagIds.Count
            ? _searchFilterTagIds[SearchFilterTagIndex - 3]
            : (long?)null;

        return new MailSearchFilter
        {
            From = SearchFilterNormalizer.From(SearchFilterFrom),
            To = SearchFilterNormalizer.From(SearchFilterTo),
            Subject = SearchFilterNormalizer.Keyword(SearchFilterSubject),
            Body = SearchFilterNormalizer.Keyword(SearchFilterBody),
            FileName = SearchFilterNormalizer.Keyword(SearchFilterFileName),
            HasAttachment = SearchFilterAttachmentIndex switch { 1 => true, 2 => false, _ => null },
            TagId = tagId,
            HasAnyTag = SearchFilterTagIndex switch { 1 => true, 2 => false, _ => null },
            HasReminder = SearchFilterReminderIndex switch { 1 => true, 2 => false, _ => null },
            Note = SearchFilterNormalizer.Keyword(SearchFilterNote),
            SinceUtc = since,
            BeforeUtc = before
        };
    }

    private static DateTimeOffset StartOfToday()
    {
        var local = DateTimeOffset.Now.Date;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    /// <summary>
    /// 应用筛选面板：条件直接进查询对象（与手写语法、关键词是「与」关系），并切到搜索结果态。
    /// 输入值先过 <see cref="SearchFilterNormalizer"/>：从正文复制来的「姓名 &lt;地址&gt;」会被收拾干净。
    /// </summary>
    [RelayCommand]
    private async Task ApplySearchFilterAsync()
    {
        _activeSearchFilter = BuildSearchFilter();

        // 内联筛选条不自动收起：方便接着从正文复制下一个条件继续加
        await SearchAsync();
    }

    /// <summary>收起筛选条（已经生效的条件保留）。</summary>
    [RelayCommand]
    private void HideSearchFilter() => IsSearchFilterOpen = false;

    [RelayCommand]
    private async Task ResetSearchFilterAsync()
    {
        ClearSearchFilterFields();
        await ApplySearchFilterAsync();
    }

    /// <summary>标签下拉：前两项固定，其后列出本地标签（按名称排序）。</summary>
    private async Task LoadSearchFilterTagsAsync()
    {
        try
        {
            var tags = await _tagService.GetAllAsync();

            while (SearchFilterTagOptions.Count > 3) SearchFilterTagOptions.RemoveAt(SearchFilterTagOptions.Count - 1);
            _searchFilterTagIds.Clear();

            foreach (var tag in tags.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                SearchFilterTagOptions.Add(tag.Name);
                _searchFilterTagIds.Add(tag.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "加载筛选器标签失败");
        }
    }

    // ---------------- 联系人卡片（单击发件人地址） ----------------

    [ObservableProperty]
    private bool _isContactCardOpen;

    [RelayCommand]
    private void ToggleContactCard() => IsContactCardOpen = !IsContactCardOpen;

    /// <summary>用该地址新建撰写。</summary>
    [RelayCommand]
    private void ComposeToAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        IsContactCardOpen = false;
        _dialogs.ShowCompose(new ComposeRequest { To = address, AccountId = Detail?.AccountId });
    }

    /// <summary>按该地址聚合往来邮件（等价于 from: 语法）。</summary>
    [RelayCommand]
    private async Task SearchBySenderAsync(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        IsContactCardOpen = false;
        SearchText = "from:" + address.Trim();
        await SearchAsync();
    }

    [RelayCommand]
    private void CopyAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        try { Clipboard.SetText(address.Trim()); StatusText = "已复制地址：" + address.Trim(); }
        catch (Exception) { }
    }
    // ---------------- 收件人卡片（单击「收件人：」整行） ----------------

    [ObservableProperty]
    private bool _isRecipientCardOpen;

    /// <summary>收件人地址列表（只含 To）：头部按地址逐个渲染交互单元。</summary>
    public IReadOnlyList<MailAddressItem> RecipientAddresses => BuildAddressItems(Detail?.To, "收件人");

    /// <summary>抄送地址列表：与收件人共用同一套交互（悬停高亮 / 左键卡片 / 右键菜单）。</summary>
    public IReadOnlyList<MailAddressItem> CcAddresses => BuildAddressItems(Detail?.Cc, "抄送");

    /// <summary>密送地址列表。</summary>
    public IReadOnlyList<MailAddressItem> BccAddresses => BuildAddressItems(Detail?.Bcc, "密送");

    public bool HasCc => Detail is { Cc.Count: > 0 };

    public bool HasBcc => Detail is { Bcc.Count: > 0 };

    private static IReadOnlyList<MailAddressItem> BuildAddressItems(
        IReadOnlyList<EmailAddress>? addresses, string group)
    {
        if (addresses is null || addresses.Count == 0) return Array.Empty<MailAddressItem>();
        return addresses.Select(a => new MailAddressItem(a.Address, a.Display, group)).ToList();
    }

    /// <summary>收件人 / 抄送 / 密送分组；卡片里按组展示，空组不出现。</summary>
    public IReadOnlyList<MailRecipientGroup> RecipientGroups
    {
        get
        {
            var groups = new List<MailRecipientGroup>();
            if (Detail is null) return groups;

            if (Detail.To.Count > 0) groups.Add(new MailRecipientGroup("收件人", Detail.To));
            if (Detail.Cc.Count > 0) groups.Add(new MailRecipientGroup("抄送", Detail.Cc));
            if (Detail.Bcc.Count > 0) groups.Add(new MailRecipientGroup("密送", Detail.Bcc));
            return groups;
        }
    }

    [RelayCommand]
    private void ToggleRecipientCard()
    {
        if (RecipientGroups.Count == 0) return;
        IsRecipientCardOpen = !IsRecipientCardOpen;
    }

    [RelayCommand]
    private Task SearchAsync()
    {
        var filterActive = _activeSearchFilter is { HasAnyCondition: true };
        if (string.IsNullOrWhiteSpace(SearchText) && !filterActive) return ClearSearchAsync();

        _pinnedEmailId = null;
        IsSearchResults = true;
        CurrentPage = 1;
        ListTitle = filterActive && string.IsNullOrWhiteSpace(SearchText) ? "筛选结果" : "搜索结果";
        return ReloadMessagesAsync();
    }

    [RelayCommand]
    private Task ClearSearchAsync()
    {
        IsSearchResults = false;
        IsSearchScopeAllMail = false;
        SearchText = string.Empty;

        // 清空搜索同时清掉「全部搜索条件」面板，避免出现「搜索框空了但结果还是被筛过」的错觉
        _activeSearchFilter = null;
        ClearSearchFilterFields();
        CurrentPage = 1;
        return ReloadMessagesAsync();
    }

    /// <summary>只清空筛选面板的输入，不触发查询（供 ClearSearch / 重置复用）。</summary>
    private void ClearSearchFilterFields()
    {
        SearchFilterFrom = "";
        SearchFilterTo = "";
        SearchFilterSubject = "";
        SearchFilterBody = "";
        SearchFilterFileName = "";
        SearchFilterAttachmentIndex = 0;
        SearchFilterTagIndex = 0;
        SearchFilterReminderIndex = 0;
        SearchFilterNote = "";
        SearchFilterTimeIndex = 0;
        SearchFilterDateFrom = null;
        SearchFilterDateTo = null;
    }

    [RelayCommand]
    private Task LoadMoreAsync() => ReloadMessagesAsync(append: true);

    /// <summary>上一页 / 下一页：整页替换列表，页码与总页数同步刷新。</summary>
    [RelayCommand]
    private Task PreviousPageAsync()
    {
        if (!CanGoPrevious) return Task.CompletedTask;
        CurrentPage--;
        return ReloadMessagesAsync();
    }

    [RelayCommand]
    private Task NextPageAsync()
    {
        if (!CanGoNext) return Task.CompletedTask;
        CurrentPage++;
        return ReloadMessagesAsync();
    }

    [RelayCommand]
    private Task FirstPageAsync()
    {
        if (CurrentPage == 1) return Task.CompletedTask;
        CurrentPage = 1;
        return ReloadMessagesAsync();
    }

    [RelayCommand]
    private Task LastPageAsync()
    {
        if (CurrentPage == TotalPages) return Task.CompletedTask;
        CurrentPage = TotalPages;
        return ReloadMessagesAsync();
    }

    partial void OnFilterChanged(MailListFilter value)
    {
        if (_suspendReload) return;
        CurrentPage = 1;
        FireAndForget.Run(() => ReloadMessagesAsync(), "刷新邮件列表");
    }

    partial void OnConversationModeChanged(bool value)
    {
        if (_suspendReload) return;
        CurrentPage = 1;
        FireAndForget.Run(() => ReloadMessagesAsync(), "刷新邮件列表");
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshSearchChips();
        // 手动修改搜索词即退出「定位邮件」模式
        _pinnedEmailId = null;
    }

    partial void OnIsSearchResultsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSearchChips));
    }

    partial void OnIsSearchScopeAllMailChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSearchScopeCurrentView));
        if (_suppressSearchReload || !IsSearchResults) return;

        CurrentPage = 1;
        FireAndForget.Run(() => ReloadMessagesAsync(), "刷新邮件列表");
    }

    // ---------------- 货代行业功能开关（设置页「货代」分区） ----------------

    /// <summary>当前生效的货代开关（总开关关闭时四项都算关）。</summary>
    public FreightFeatureFlags FreightFlags => FreightFeatureFlags.From(_settings.Current);

    /// <summary>左栏「业务单号」聚合入口是否显示。</summary>
    public bool ShowFreightReferences => FreightFlags.References;

    /// <summary>左栏「时效节点」看板是否显示。</summary>
    public bool ShowFreightDeadlines => FreightFlags.Deadlines;

    /// <summary>左栏「等待回复」入口与右键菜单项是否显示。</summary>
    public bool ShowFreightFollowUps => FreightFlags.FollowUps;

    /// <summary>邮件头「邮件类型」标签是否显示（开关 + 当前邮件识别出了类型）。</summary>
    public bool ShowDetailIndustryType => FreightFlags.MailTypes && Detail is { HasIndustryType: true };

    /// <summary>设置保存后重算货代相关可见性（并清掉关闭项残留的看板数据）。</summary>
    private void RaiseFreightVisibility()
    {
        OnPropertyChanged(nameof(FreightFlags));
        OnPropertyChanged(nameof(ShowFreightReferences));
        OnPropertyChanged(nameof(ShowFreightDeadlines));
        OnPropertyChanged(nameof(ShowFreightFollowUps));
        OnPropertyChanged(nameof(ShowDetailIndustryType));
        OnPropertyChanged(nameof(HasDetailReferences));
        OnPropertyChanged(nameof(HasDetailDeadlines));
    }

    /// <summary>按当前开关清空货代看板：关闭的项不再展示，重新开启后会重新加载。</summary>
    private void ClearDisabledFreightBoards()
    {
        var flags = FreightFlags;

        if (!flags.References && BusinessRefs.Count > 0)
        {
            BusinessRefs.Clear();
            BusinessRefsSummary = "";
        }
        if (!flags.Deadlines && Deadlines.Count > 0)
        {
            Deadlines.Clear();
            DeadlineSummary = "";
        }
        if (!flags.References && DetailReferences.Count > 0) DetailReferences = Array.Empty<FreightReference>();
        if (!flags.References)
        {
            // 列表行的业务单号徽标也要立刻消失（数据还在库里，重新开启后按页查回来）
            foreach (var row in Messages)
            {
                if (row.References.Count > 0) row.References = Array.Empty<FreightReference>();
            }
        }

        // 「邮件类型」关掉后，列表里的异常角标同样立即隐藏
        foreach (var row in Messages) row.IndustryTypeVisible = flags.MailTypes;

        if (!flags.Deadlines && DetailDeadlines.Count > 0) DetailDeadlines = Array.Empty<DeadlineBoardItem>();
        if (!flags.References && DetailCaseId > 0) DetailCaseId = 0;
    }

    // ---------------- 陌生发件人 ----------------

    /// <summary>阅读区是否显示陌生提示（开关打开 + 当前这封确实是首次来信）。</summary>
    public bool ShowDetailStranger => _settings.Current.MarkStrangerSenders && Detail is { IsStranger: true };

    /// <summary>当前这封邮件的发件人是否已经被加进通讯录（提示条按钮的状态）。</summary>
    [ObservableProperty]
    private bool _strangerJoined;

    /// <summary>设置保存后把开关状态同步到列表行：数据还在库里，重新开启立刻恢复显示。</summary>
    private void ApplyStrangerBadgeVisibility()
    {
        var enabled = _settings.Current.MarkStrangerSenders;
        foreach (var row in Messages) row.StrangerBadgeEnabled = enabled;
        OnPropertyChanged(nameof(ShowDetailStranger));

        var bounceEnabled = _settings.Current.MarkBounceMails;
        foreach (var row in Messages) row.BounceBadgeEnabled = bounceEnabled;
        OnPropertyChanged(nameof(ShowDetailBounce));
    }

    // ---------------- 退信 ----------------

    /// <summary>阅读区是否显示退信提示（开关打开 + 当前这封确实是投递失败退回的邮件）。</summary>
    public bool ShowDetailBounce => _settings.Current.MarkBounceMails && Detail is { IsBounce: true };

    /// <summary>退信提示条第二行：失败收件人 / 状态码 / 诊断，没有细节时引导看正文。</summary>
    public string DetailBounceLine => Detail?.BounceDetailLine ?? "";

    /// <summary>
    /// 把陌生发件人加入通讯录：只影响以后的来信——按规格，当前这封邮件的陌生标识保持不变。
    /// </summary>
    [RelayCommand]
    private async Task JoinStrangerContactsAsync()
    {
        if (Detail is not { IsStranger: true } detail) return;

        var email = (detail.FromEmail ?? string.Empty).Trim().ToLowerInvariant();
        if (email.Length == 0 || !email.Contains('@', StringComparison.Ordinal))
        {
            StatusText = "这封邮件的发件人地址无效，无法加入通讯录";
            return;
        }

        try
        {
            // 走导入口径：不累加往来次数，只把地址与显示名写进联系人表
            await _contacts.UpsertManyAsync(new[]
            {
                new Contact { Email = email, Name = (detail.FromName ?? string.Empty).Trim() }
            });

            StrangerJoined = true;
            StatusText = $"已加入通讯录：{email}（以后的来信不再标为陌生）";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加入通讯录失败 {Email}", email);
            StatusText = "加入通讯录失败：" + ex.Message;
        }
    }

    // ---------------- 仿冒域名（钓鱼警示） ----------------

    /// <summary>自有企业域名（去掉公共邮箱域）；账户刷新时重算。</summary>
    private IReadOnlySet<string> _ownDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前邮件命中的仿冒域名结论（呈现时算，缓存一份避免多个绑定重复计算）。</summary>
    private PhishingDomainVerdict? _detailPhishing;

    /// <summary>阅读区是否显示红色钓鱼警示条。</summary>
    public bool ShowDetailPhishing => _detailPhishing is not null;

    /// <summary>钓鱼警示的说明文案（与哪个自有域名相像、命中原因）。</summary>
    public string DetailPhishingHint => _detailPhishing?.Summary ?? "";

    private void RefreshOwnDomains()
        => _ownDomains = PhishingDomainPolicy.OwnDomains(Accounts.Select(node => node.Model.Email));

    /// <summary>按当前开关与容差算一封邮件的结论；开关关闭（或没有企业域名）时一律 null。</summary>
    private PhishingDomainVerdict? EvaluatePhishing(string? fromEmail)
    {
        if (!_settings.Current.PhishingDomainWarning) return null;
        if (_ownDomains.Count == 0) return null;

        return PhishingDomainPolicy.Evaluate(fromEmail, _ownDomains, _settings.Current.PhishingDomainTolerance);
    }

    /// <summary>设置保存后对当前页与阅读区重算：改容差 / 开关立即生效，不需要重新收信。</summary>
    private void ApplyPhishingVisibility()
    {
        foreach (var row in Messages) row.Phishing = EvaluatePhishing(row.Model.FromEmail);
        RefreshDetailPhishing();
    }

    private void RefreshDetailPhishing()
    {
        _detailPhishing = Detail is null ? null : EvaluatePhishing(Detail.FromEmail);
        OnPropertyChanged(nameof(ShowDetailPhishing));
        OnPropertyChanged(nameof(DetailPhishingHint));
    }

    partial void OnDetailChanged(EmailDetail? value)
    {
        // 远程图片放行只对当前这封邮件生效
        _loadRemoteImages = false;

        // 换邮件就收起上一封的摘要，避免张冠李戴
        Summary.Reset();

        RefreshDetailPhishing();
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailAvatarBrush));
        OnPropertyChanged(nameof(HasDetailAvatar));
        OnPropertyChanged(nameof(RecipientGroups));
        OnPropertyChanged(nameof(RecipientAddresses));
        OnPropertyChanged(nameof(CcAddresses));
        OnPropertyChanged(nameof(BccAddresses));
        OnPropertyChanged(nameof(HasCc));
        OnPropertyChanged(nameof(HasBcc));
        OnPropertyChanged(nameof(VisibleAttachments));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(InlineImagePaths));
        OnPropertyChanged(nameof(BodyDisplay));
        OnPropertyChanged(nameof(SizeDisplayText));
        OnPropertyChanged(nameof(BodyHtmlSource));
        RefreshBodyDocument();
        OnPropertyChanged(nameof(ShowDetailLoading));
        OnPropertyChanged(nameof(ShowDetailPlaceholder));
        OnPropertyChanged(nameof(ShowDetailIndustryType));
        OnPropertyChanged(nameof(ShowDetailStranger));
        OnPropertyChanged(nameof(ShowDetailBounce));
        OnPropertyChanged(nameof(DetailBounceLine));
        OnPropertyChanged(nameof(CanReplyOrForward));
        OnPropertyChanged(nameof(ShowReloadBodyButton));
        OnPropertyChanged(nameof(ShowBodyContent));
        StrangerJoined = false;
    }

    partial void OnIsDetailLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDetailLoading));
        OnPropertyChanged(nameof(ShowDetailPlaceholder));
    }

    partial void OnSelectedRowChanged(MailRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanReplyOrForward));
        OnPropertyChanged(nameof(ReadToggleText));
        OnPropertyChanged(nameof(IsSelectedStarred));
        OnPropertyChanged(nameof(IsSelectedUnread));
        FireAndForget.Run(() => LoadDetailAsync(value), "加载邮件详情");
    }

    private MessageQuery BuildQuery(int skip)
    {
        var filterActive = _activeSearchFilter is { HasAnyCondition: true };
        var searchActive = filterActive || (IsSearchResults && !string.IsNullOrWhiteSpace(SearchText));
        var query = new MessageQuery
        {
            Skip = skip,
            Take = _settings.Current.ListPageSize,
            Filter = Filter,
            SearchText = searchActive ? SearchText : null
        };

        if (searchActive && IsSearchScopeAllMail)
        {
            // 搜索结果「所有邮件」：跨账户 / 文件夹，排除垃圾箱，避免把已丢弃邮件混进来。
            query.ExcludeTrash = true;
        }
        else if (_currentTag is { } tag)
        {
            // 标签视图：跨文件夹聚合，垃圾箱仍默认排除以免误读已丢弃邮件。
            query.TagId = tag.Id;
            query.ExcludeTrash = true;
        }
        else if (_currentFolder is not null)
        {
            query.FolderId = _currentFolder.Id;
            query.ExcludeTrash = _currentFolder.Kind != FolderKind.Trash;
        }
        else if (_currentQuickView?.ScopeKind is { } kind)
        {
            query.FolderKind = kind;
            query.ExcludeTrash = kind != FolderKind.Trash;
        }

        // 从跟进 / 案件窗口定位：只显示这一封，且不受当前视图的垃圾箱排除影响
        if (!searchActive && _pinnedEmailId is { } pinned)
        {
            query.EmailId = pinned;
            query.ExcludeTrash = false;
        }

        // 「全部搜索条件」面板：结构化条件与关键词 / 手写语法是「与」关系
        _activeSearchFilter?.ApplyTo(query);

        return query;
    }

    private async Task ReloadMessagesAsync(bool append = false)
    {
        var version = ++_loadVersion;
        if (!append) ClearBatchSelection();

        // 首屏（或列表本来为空）才显示加载占位；切换文件夹 / 搜索时保留旧列表，避免每次闪一下。
        var showLoading = !append && Messages.Count == 0;
        if (showLoading)
        {
            IsListLoading = true;
            IsEmpty = false;
        }

        var pageSize = Math.Clamp(_settings.Current.ListPageSize, 10, 500);
        var skip = (CurrentPage - 1) * pageSize;
        var query = BuildQuery(skip);

        try
        {
            var rows = new List<MailRow>();
            int total;

            // SQLite 的「异步」底层同步执行：查询放到线程池，界面线程只等结果（行对象仍在 UI 线程构建）
            var queryWatch = Stopwatch.StartNew();

            if (ConversationMode)
            {
                var page = await Task.Run(() => _messages.QueryThreadsAsync(query)).ConfigureAwait(true);
                rows.AddRange(page.Items.Select(g => new MailRow(g.Representative, g.Count, _avatars)));
                total = page.TotalCount;
                HasMore = page.HasMore;
            }
            else
            {
                var page = await Task.Run(() => _messages.QueryAsync(query)).ConfigureAwait(true);
                rows.AddRange(page.Items.Select(s => new MailRow(s, avatars: _avatars)));
                total = page.TotalCount;
                HasMore = page.HasMore;
            }

            if (queryWatch.ElapsedMilliseconds >= 150)
            {
                _logger.LogInformation("列表查询耗时 {Ms}ms（{Count} 行/共 {Total} 封）",
                    queryWatch.ElapsedMilliseconds, rows.Count, total);
            }

            if (version != _loadVersion) return; // 已有更新的加载请求，丢弃本次结果

            StartupTrace.Mark($"列表查询 {rows.Count} 行/共 {total} 封");

            TotalCount = total;
            // 整批替换：逐条 Add 会给 ListBox 发 500 次通知，每次都要重排
            Messages.ReplaceAll(rows);
            StartupTrace.Mark("列表 填行");

            IsEmpty = Messages.Count == 0;
            var countText = ConversationMode
                ? $"{total} 个会话 · 第 {CurrentPage}/{TotalPages} 页"
                : $"共 {total} 封 · 第 {CurrentPage}/{TotalPages} 页";
            ListSubtitle = IsSearchResults
                ? (IsSearchScopeAllMail ? "所有邮件 · " : "当前文件夹 · ") + countText
                : countText;

            // 列表先上屏：标签 / 业务单号徽标另起后台任务补齐，不挡首屏。
            IsListLoading = false;
            if (Messages.Count > 0) SelectedRow = Messages[0];
            else if (SelectedRow is null) await LoadDetailAsync(null); // 本来就没有选中行时，属性不会触发变更
            else SelectedRow = null;
            StartupTrace.Mark("列表 选中");

            await LetUiRenderAsync();
            StartupTrace.Mark("列表上屏");
            FireAndForget.Run(() => LoadRowBadgesAsync(rows, version), "补齐行徽标");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载邮件列表失败");
            StatusText = "加载邮件列表失败：" + ex.Message;
            IsEmpty = Messages.Count == 0;
        }
        finally
        {
            if (showLoading) IsListLoading = false;
        }
    }

    /// <summary>批量补齐列表徽标（标签 / 业务单号）；在列表上屏之后执行，失败只记日志。</summary>
    private async Task LoadRowBadgesAsync(List<MailRow> rows, int version)
    {
        if (rows.Count == 0) return;
        await LetUiRenderAsync();

        var ids = rows.Select(row => row.Id).ToList();

        // 货代「邮件类型」关掉后，异常角标也不该再出现
        var showIndustryType = FreightFlags.MailTypes;
        foreach (var row in rows) row.IndustryTypeVisible = showIndustryType;

        // 陌生发件人角标同样跟随设置开关（数据在库里，重新开启后立刻恢复）
        var showStranger = _settings.Current.MarkStrangerSenders;
        foreach (var row in rows) row.StrangerBadgeEnabled = showStranger;

        // 退信角标同理
        var showBounce = _settings.Current.MarkBounceMails;
        foreach (var row in rows) row.BounceBadgeEnabled = showBounce;

        // 仿冒域名按当前开关与容差算一遍（纯内存判定，历史邮件同样适用）
        foreach (var row in rows) row.Phishing = EvaluatePhishing(row.Model.FromEmail);

        try
        {
            var tagMap = await _tagService.GetForMessagesAsync(ids);
            if (version != _loadVersion) return;
            foreach (var row in rows)
            {
                if (tagMap.TryGetValue(row.Id, out var tags)) row.Tags = tags;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "加载邮件标签失败");
        }

        // 货代功能关闭时不查业务单号：列表徽标要跟着消失，也省掉一次按页查询
        if (!FreightFlags.References)
        {
            foreach (var row in rows)
            {
                if (row.References.Count > 0) row.References = Array.Empty<FreightReference>();
            }

            return;
        }

        try
        {
            var refMap = await _referenceRepository.GetForMessagesAsync(ids);
            if (version != _loadVersion) return;
            foreach (var row in rows)
            {
                if (refMap.TryGetValue(row.Id, out var refs)) row.References = refs;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "加载业务单号失败");
        }
    }

    /// <summary>
    /// 详情加载分两步：先读本地邮件头立刻上屏（主题 / 发件人 / 收件人 / 附件 / 业务单号 / 时效），
    /// 再在后台按需下载正文；正文区自己显示加载态，整页不会为了等正文而不显示邮件头。
    /// </summary>
    private async Task LoadDetailAsync(MailRow? row)
    {
        var version = _detailLoader.Begin();

        // 选中下一封即取消上一封还在进行的正文下载：快速翻邮件不会堆出一串并发连接
        var previous = Interlocked.Exchange(ref _detailCts, null);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _detailCts = cts;
        var ct = cts.Token;

        if (row is null)
        {
            Detail = null;
            NoteText = "";
            CanSendReadReceipt = false;
            DetailReferences = Array.Empty<FreightReference>();
            DetailDeadlines = Array.Empty<DeadlineBoardItem>();
            DetailCaseId = 0;
            IsDetailLoading = false;
            IsBodyLoading = false;
            return;
        }

        // 先进入加载态并清掉上一封，避免新内容到达前阅读窗格一直显示旧邮件
        Detail = null;
        IsDetailLoading = true;
        IsBodyLoading = false;

        try
        {
            // 第一步：本地邮件头（纯数据库读取，通常毫秒级）
            var header = await _detailLoader.LoadHeaderAsync(row.Id, version, ct);
            if (header is null || !_detailLoader.IsCurrent(version)) return;

            Detail = header;
            IsDetailLoading = false; // 邮件头已上屏，整页加载态结束

            // 正文没下载就先让正文区进入加载态，避免中间那一瞬闪出「暂无正文内容」
            var needsBody = header.BodyState != BodyState.Downloaded;
            if (needsBody) IsBodyLoading = true;

            // 先把邮件头画出来，再读标签/备注/往来邮件等附加信息；否则这些同步完成的查询会继续占着 UI 线程。
            await LetUiRenderAsync();

            await LoadDetailMetaAsync(header);

            if (row.IsUnread)
            {
                await MarkReadAsync(row);
            }

            if (!needsBody) return;

            // 第二步：正文按需下载（邮件头已经可读，不必等）
            if (!_detailLoader.IsCurrent(version)) return;
            try
            {
                var full = await _detailLoader.LoadBodyAsync(header, version, text => StatusText = text, ct);
                if (full is null || !_detailLoader.IsCurrent(version)) return;
                if (!ReferenceEquals(full, header)) Detail = full;
            }
            finally
            {
                if (_detailLoader.IsCurrent(version)) IsBodyLoading = false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 被下一次选择取代：静默收尾，不覆盖状态栏，也不当成错误
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取邮件 {Id} 详情失败", row.Id);
            StatusText = "读取邮件失败：" + ex.Message;
        }
        finally
        {
            // 已有更新的加载请求时由新的那次负责收尾，避免提前关掉加载态
            if (_detailLoader.IsCurrent(version))
            {
                IsDetailLoading = false;
                IsBodyLoading = false;
            }
        }
    }

    /// <summary>
    /// 邮件头就能决定的展示信息：标签、备注、业务单号、时效节点、阅读收条。
    /// 与正文下载解耦，保证「先看到的头信息」是完整的。
    /// </summary>
    private async Task LoadDetailMetaAsync(EmailDetail detail)
    {
        await LoadContextSidebarAsync(detail);

        try { DetailTags = await _tagService.GetForMessageAsync(detail.Id); }
        catch (Exception ex) { _logger.LogDebug(ex, "读取邮件标签失败"); DetailTags = Array.Empty<MailTag>(); }

        try { NoteText = await _notes.GetAsync(detail.Id); }
        catch (Exception ex) { _logger.LogDebug(ex, "读取邮件备注失败"); NoteText = ""; }

        // 业务单号：邮件头同步时已抽取；若为空（旧数据）则按当前正文补一次
        try
        {
            var references = await _referenceRepository.GetForMessageAsync(detail.Id);
            if (references.Count == 0 && detail.BodyState == BodyState.Downloaded)
            {
                await _referenceService.IndexMessageAsync(detail);
                references = await _referenceRepository.GetForMessageAsync(detail.Id);
            }
            DetailReferences = references;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取业务单号失败");
            DetailReferences = Array.Empty<FreightReference>();
        }

        // 时效节点与所属一票货：详情卡直接展示「距截关几小时」与「打开这票货」
        try
        {
            DetailDeadlines = await _shipments.GetDeadlinesForMessageAsync(detail.Id);
            DetailCaseId = await _shipments.GetCaseIdForMessageAsync(detail.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取时效节点失败");
            DetailDeadlines = Array.Empty<DeadlineBoardItem>();
            DetailCaseId = 0;
        }

        try
        {
            var requested = !string.IsNullOrWhiteSpace(detail.MdnTo)
                && !await _receipts.IsSentAsync(detail.Id);
            CanSendReadReceipt = requested;

            // 用户开启“自动发送回执”时静默回复；失败则保留提示条，由用户手动处理。
            if (requested && _settings.Current.AutoSendReadReceipt)
            {
                await SendReadReceiptAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取/自动发送阅读收条失败");
            CanSendReadReceipt = false;
        }
    }

    private Task MarkReadAsync(MailRow row) => SetReadStateAsync(row, read: true, showStatus: false);

    /// <summary>设置单封邮件的已读状态，并同步本地库、侧栏计数与服务器。</summary>
    private async Task SetReadStateAsync(MailRow row, bool read, bool showStatus = true)
    {
        if (row.IsUnread == !read) return;

        var confirmed = await _mailActions.SetSeenAsync(row.Model, read);

        row.IsUnread = !read;
        AdjustFolderUnread(row.Model.FolderId, read ? -1 : 1);

        if (showStatus)
        {
            StatusText = confirmed
                ? (read ? "已标记为已读" : "已标记为未读")
                : "已在本地更新已读状态，等待同步服务器";
        }
        OnPropertyChanged(nameof(ReadToggleText));
        OnPropertyChanged(nameof(IsSelectedStarred));
        OnPropertyChanged(nameof(IsSelectedUnread));
        await RefreshPendingStateAsync();
        await RefreshLocalTotalAsync();
    }

    /// <summary>按文件夹 id 调整侧栏未读合计（统一收件箱下列表可能跨文件夹）。</summary>
    private void AdjustFolderUnread(long folderId, int delta)
    {
        if (delta == 0) return;

        foreach (var account in Accounts)
        {
            var folder = account.Folders.FirstOrDefault(f => f.Id == folderId);
            if (folder is null) continue;

            folder.UnreadCount = Math.Max(0, folder.UnreadCount + delta);
            account.RefreshUnread();
            return;
        }
    }

    // ---------------- 文件夹管理 ----------------

    /// <summary>新建文件夹；未指定父节点时建在该账户根目录。</summary>
    [RelayCommand]
    private Task CreateFolderAsync(FolderNode? parent)
    {
        var account = Accounts.FirstOrDefault(a => a.Id == parent?.AccountId) ?? Accounts.FirstOrDefault();
        if (account is null)
        {
            StatusText = "请先配置一个邮箱账户";
            return Task.CompletedTask;
        }

        if (!_dialogs.ShowFolderEditor(new FolderEditRequest
            {
                AccountId = account.Id,
                AccountName = account.Title,
                Parent = parent?.Model
            }))
        {
            return Task.CompletedTask;
        }

        return ReloadAccountsAsync();
    }

    /// <summary>
    /// 从账户节点右键新建文件夹：建在该账户根目录下。
    /// 与文件夹节点的「新建子文件夹」共用文件夹编辑器，只是 parent 为空、账户由节点决定。
    /// </summary>
    [RelayCommand]
    private async Task CreateFolderForAccountAsync(AccountNode? node)
    {
        if (node is null) return;

        if (!_dialogs.ShowFolderEditor(new FolderEditRequest
            {
                AccountId = node.Id,
                AccountName = node.Title,
                Parent = null
            }))
        {
            return;
        }

        await ReloadAccountsAsync();
        StatusText = $"已在「{node.Title}」下新建文件夹";
    }

    /// <summary>从账户节点右键进入设置：定位到账户分区并预选该账户。</summary>
    [RelayCommand]
    private async Task OpenAccountSettingsAsync(AccountNode? node)
    {
        if (node is null) return;

        _dialogs.ShowSettings("accounts", node.Id);
        await ReloadAccountsAsync();
        await SelectCurrentViewAsync();
        await RefreshLocalTotalAsync();
        ApplyPhishingVisibility();
    }

    /// <summary>
    /// 从账户节点右键导入 .eml：先把文件读进来，再让用户选目标文件夹（默认该账户收件箱），
    /// 复用设置里那套 IMailImportService（按 Message-ID 去重）。
    /// </summary>
    [RelayCommand]
    private async Task ImportEmlForAccountAsync(AccountNode? node)
    {
        if (node is null) return;

        var dialog = new OpenFileDialog
        {
            Title = $"导入 .eml 到「{node.Title}」",
            Multiselect = true,
            Filter = "邮件文件 (*.eml)|*.eml|所有文件|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        var files = dialog.FileNames.Where(File.Exists).ToArray();
        if (files.Length == 0) return;

        var targets = node.Folders
            .Where(folder => folder.CanReceiveMailDrop)
            .Select(folder => new MoveTargetItem(node.Title, folder.Model))
            .ToList();

        var picked = _dialogs.PickMoveTarget(targets, node.Folders.FirstOrDefault(f => f.Kind == FolderKind.Inbox)?.Model);
        if (picked is null) return;

        try
        {
            var result = await _import.ImportAsync(node.Id, picked.Id, files);
            StatusText = result.Summary;

            await ReloadAccountsAsync();
            await SelectCurrentViewAsync();
            await RefreshLocalTotalAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "账户右键导入 .eml 失败：{AccountId}", node.Id);
            StatusText = "导入失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RenameFolderAsync(FolderNode? folder)
    {
        if (folder is not { IsUserFolder: true } node) return;

        var account = Accounts.FirstOrDefault(a => a.Id == node.AccountId);
        if (account is null) return;

        if (_dialogs.ShowFolderEditor(new FolderEditRequest
            {
                AccountId = node.AccountId,
                AccountName = account.Title,
                Existing = node.Model
            }))
        {
            await ReloadAccountsAsync();
            StatusText = "文件夹已重命名";
        }
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(FolderNode? folder)
    {
        if (folder is not { IsUserFolder: true } node) return;

        var confirm = _dialogs.Confirm(
            "删除文件夹",
            $"确定删除文件夹「{node.Title}」吗？",
            primaryText: "删除",
            detail: "仅空文件夹可以被删除，删除后不可恢复。",
            icon: DialogIcon.Warning,
            danger: true);
        if (!confirm) return;

        var result = await _folderService.DeleteAsync(node.Model);
        if (!result.Success)
        {
            StatusText = result.Error ?? "删除文件夹失败";
            return;
        }

        await ReloadAccountsAsync();
        await ReloadSidebarTagsAsync();
        await SelectQuickViewAsync(QuickViews[0]);
        StatusText = $"已删除文件夹「{node.Title}」";
    }

    /// <summary>把拖拽的一封或多封邮件移动到目标文件夹，成功后从当前列表移除。</summary>
    public async Task MoveDroppedMessagesAsync(IReadOnlyList<long> messageIds, FolderNode target)
    {
        if (messageIds.Count == 0 || !target.Model.IsSelectable) return;

        var rows = Messages.Where(row => messageIds.Contains(row.Id)).ToList();
        if (rows.Count == 0) return;

        await MoveRowsToFolderAsync(rows, target.Model);
    }

    /// <summary>通过"移动到文件夹"菜单选择目标并移动当前（或批量选中的）邮件。</summary>
    [RelayCommand]
    private async Task MoveToFolderAsync()
    {
        var rows = SelectedRowsForAction();
        if (rows.Count == 0) return;

        var accountId = rows[0].Model.AccountId;
        if (rows.Any(row => row.Model.AccountId != accountId))
        {
            StatusText = "请选择同一账户内的邮件再移动";
            return;
        }

        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return;

        var targets = account.Folders
            .Where(folder => folder.CanReceiveMailDrop)
            .Select(folder => new MoveTargetItem(account.Title, folder.Model))
            .ToList();

        var picked = _dialogs.PickMoveTarget(targets, _currentFolder?.Model);
        if (picked is null) return;

        await MoveRowsToFolderAsync(rows, picked);
    }

    /// <summary>为当前（或批量选中的）邮件打标签；窗口内可直接新建标签。</summary>
    [RelayCommand]
    private async Task TagSelectedAsync()
    {
        var rows = SelectedRowsForAction();
        if (rows.Count == 0) return;

        // 标签按账户隔离：选择器只列当前账户（跨账户多选时以第一封为准）的标签
        var accountId = rows[0].Model.AccountId;
        var tags = await _tagService.GetByAccountAsync(accountId);
        var emailIds = rows.Select(row => row.Id).ToList();
        var applied = rows.Count == 1
            ? (await _tagService.GetForMessageAsync(emailIds[0])).Select(tag => tag.Id).ToHashSet()
            : new HashSet<long>();

        var window = new Views.TagPickerWindow(_tagService, emailIds, tags, applied, accountId);
        if (Views.WindowOwnerHelper.ShowDialog(window) == true)
        {
            StatusText = $"已更新 {rows.Count} 封邮件的标签";
            await ReloadMessagesAsync();
            // 标签窗口内可能新建了标签，侧栏需要同步刷新。
            await ReloadSidebarTagsAsync();
        }
    }

    /// <summary>
    /// 对当前邮件生成 AI 摘要（本机小模型；模型不可用时自动退回规则抽取）。
    /// 只读操作，不标记已读、不写库。
    /// </summary>
    [RelayCommand]
    private async Task SummarizeAsync() => await Summary.ShowAsync(Detail).ConfigureAwait(true);

    /// <summary>
    /// 查看当前选中邮件的源码：优先服务器原始 MIME，取不到回退本地重建。
    /// 只读操作，不改变已读/星标/选中状态。
    /// </summary>
    [RelayCommand]
    private void ViewMailSource()
    {
        if (SelectedRow is not { } row)
        {
            StatusText = "请先选择一封邮件";
            return;
        }

        _mailSourceWindows.Show(row.Id, row.Subject);
    }

    /// <summary>把选中邮件标记为待办（含截止日期）。</summary>
    [RelayCommand]
    private async Task ToggleTodoAsync()
    {
        if (SelectedRow is not { } row) return;

        var existing = await _todoService.GetByEmailAsync(row.Id);
        var window = new Views.TodoEditWindow(_todoService, row.Id, row.Subject, existing);
        if (Views.WindowOwnerHelper.ShowDialog(window) == true)
        {
            StatusText = $"已标记待办：{window.Result?.Title}";
        }
    }
    /// <summary>回复发件人的阅读收条请求（MDN）。</summary>
    [RelayCommand]
    private async Task SendReadReceiptAsync()
    {
        if (Detail is not { } detail || string.IsNullOrWhiteSpace(detail.MdnTo)) return;
        if (Accounts.FirstOrDefault(node => node.Id == detail.AccountId) is not { } node)
        {
            StatusText = "找不到该邮件所属账户，无法发送回执";
            return;
        }

        try
        {
            await _receiptService.SendAsync(node.Model, detail);
            await _receipts.MarkSentAsync(detail.Id);
            CanSendReadReceipt = false;
            StatusText = "已发送阅读回执";
        }
        catch (Exception ex)
        {
            StatusText = "发送阅读回执失败：" + ex.Message;
        }
    }

    /// <summary>忽略本次阅读收条请求（不记录为已发送，下次打开仍会提示）。</summary>
    [RelayCommand]
    private void DismissReadReceipt()
    {
        CanSendReadReceipt = false;
        StatusText = "已忽略本次回执提示";
    }
    /// <summary>该单键是否被某个标签占用；窗口级按键处理先问这里，未绑定的键不拦截。</summary>
    public bool HasTagShortcut(string? key)
        => TagShortcutKeys.Normalize(key) is { } normalized
           && _tagShortcutCache.Any(tag => tag.MatchesShortcut(normalized));

    /// <summary>
    /// 标签快捷键：给选中的邮件打 / 取消标签。
    /// 全部已打则统一移除，否则统一补上；跨账户多选时每封邮件用自己的账户标签。
    /// </summary>
    public async Task ToggleTagByShortcutAsync(string key)
    {
        var rows = SelectedRowsForAction();
        if (rows.Count == 0)
        {
            StatusText = $"请先在列表里选中邮件，再按 {key} 打 / 取消标签";
            return;
        }

        var targets = rows.Select(row => (row.Id, row.Model.AccountId)).ToList();

        TagToggleResult result;
        try
        {
            result = await _tagService.ToggleByShortcutAsync(targets, key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "按快捷键切换标签失败：{Key}", key);
            StatusText = "切换标签失败：" + ex.Message;
            return;
        }

        StatusText = result.Describe();
        if (!result.Changed) return;

        var selectedId = SelectedRow?.Id;
        await ReloadMessagesAsync();
        await ReloadSidebarTagsAsync();

        // 标签视图下可能整行消失，邮件头标签条按 id 重新读一次
        if (selectedId is { } id)
        {
            try { DetailTags = await _tagService.GetForMessageAsync(id); }
            catch (Exception ex) { _logger.LogDebug(ex, "刷新邮件标签失败"); }
        }
    }

    /// <summary>侧边栏标签视图：跨文件夹按标签聚合邮件。</summary>
    [RelayCommand]
    private async Task SelectTagAsync(MailTag? tag)
    {
        if (tag is null) return;

        foreach (var quick in QuickViews) quick.IsSelected = false;
        foreach (var account in Accounts)
        {
            foreach (var folder in account.Folders) folder.IsSelected = false;
        }

        _currentTag = tag;
        _currentFolder = null;
        _currentQuickView = null;
        _pinnedEmailId = null;
        IsSearchResults = false;
        IsSearchScopeAllMail = false;
        CurrentPage = 1;
        ListTitle = $"标签：{tag.Name}";

        RefreshSidebarTagsForContext();
        await ReloadMessagesAsync();
    }

    /// <summary>刷新侧边栏标签列表（标签增删改后调用），同时更新快捷键缓存，避免重复查库。</summary>
    public async Task ReloadSidebarTagsAsync()
    {
        try
        {
            var tags = await _tagService.GetAllAsync();
            _tagShortcutCache = tags;
            RefreshSidebarTagsForContext(tags);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "加载侧边栏标签失败");
        }
    }

    /// <summary>
    /// 侧栏只显示当前视图所属账户的标签（标签按账户隔离）；
    /// 统一收件箱、标签视图等跨账户场景显示全部。
    /// </summary>
    private void RefreshSidebarTagsForContext(IReadOnlyList<MailTag>? tags = null)
    {
        var all = tags ?? _tagShortcutCache;
        var accountId = _currentFolder?.AccountId ?? _currentTag?.AccountId;

        SidebarTags.Clear();
        foreach (var tag in all)
        {
            if (accountId is { } id && tag.AccountId != id) continue;
            SidebarTags.Add(tag);
        }
    }

    private async void OnTagsChanged(object? sender, EventArgs e)
    {
        await ReloadSidebarTagsAsync();
    }
    /// <summary>加载右侧上下文面板：同一发件人的往来邮件与附件。</summary>
    private async Task LoadContextSidebarAsync(EmailDetail detail)
    {
        try
        {
            Correspondence = await _context.GetCorrespondenceAsync(detail.AccountId, detail.FromEmail, detail.Id);
            SenderAttachments = await _context.GetSenderAttachmentsAsync(detail.AccountId, detail.FromEmail);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "加载邮件上下文侧边栏失败");
            Correspondence = Array.Empty<CorrespondenceItem>();
            SenderAttachments = Array.Empty<SenderAttachmentItem>();
        }
    }

    /// <summary>点击往来邮件：在列表定位到该邮件（不在当前页则提示）。</summary>
    [RelayCommand]
    private async Task ShowCorrespondenceAsync(CorrespondenceItem? item)
    {
        if (item is null) return;

        var row = Messages.FirstOrDefault(message => message.Id == item.Id);
        if (row is not null)
        {
            SelectedRow = row;
            return;
        }

        // 不在当前页/当前文件夹：直接加载该邮件详情，避免“点了没反应”。
        try
        {
            var detail = await _messages.GetDetailAsync(item.Id);
            if (detail is null)
            {
                StatusText = "该往来邮件已在本地清理";
                return;
            }

            // 走与列表选中完全相同的加载流程，才会触发正文按需下载。
            SelectedRow = null;
            await LoadDetailAsync(new MailRow(detail, avatars: _avatars));
            StatusText = $"正在查看往来邮件：{detail.SubjectDisplay}";
        }
        catch (Exception ex)
        {
            StatusText = "打开往来邮件失败：" + ex.Message;
        }
    }

    /// <summary>点击侧边栏附件：本地已有则预览，否则提示需要先下载。</summary>
    [RelayCommand]
    private void OpenSenderAttachment(SenderAttachmentItem? item)
    {
        if (item is null) return;
        if (!item.IsAvailable)
        {
            StatusText = "附件尚未下载到本地，请先打开对应邮件下载";
            return;
        }
        try
        {
            if (_settings.Current.UseInternalAttachmentPreview
                && Views.AttachmentPreviewWindow.CanPreview(item.FileName)
                && _dialogs.ShowAttachmentPreview(item.FileName, item.LocalPath!))
            {
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.LocalPath!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打开附件失败：" + ex.Message;
        }
    }
    private List<MailRow> SelectedRowsForAction()
        => HasBatchSelection
            ? Messages.Where(item => item.IsBatchSelected).ToList()
            : SelectedRow is { } selected ? new List<MailRow> { selected } : new List<MailRow>();

    private async Task MoveRowsToFolderAsync(IReadOnlyList<MailRow> rows, MailFolder target)
    {
        if (rows.Any(row => row.Model.AccountId != target.AccountId))
        {
            StatusText = "暂不支持跨账户移动";
            return;
        }

        // 拖回原文件夹：这些邮件本来就在目标里，别把它们从列表里抹掉
        var movableRows = rows.Where(row => row.Model.FolderId != target.Id).ToList();
        if (movableRows.Count == 0)
        {
            StatusText = $"这些邮件已经在「{target.Title}」里";
            return;
        }

        // 本地先落地、界面立刻收手：把「置 \Deleted + 入队」放进线程池一次做完（两条 SQL），
        // 再把行从列表移走；IMAP 回写放后台跑，用户不用等连接与 MOVE 往返。
        MailBatchMoveHandle handle;
        try
        {
            var models = movableRows.Select(row => row.Model).ToList();
            handle = await Task.Run(() => _mailActions.PrepareMoveToFolderBatchAsync(models, target));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量移动（本地生效）失败");
            StatusText = "移动失败：" + ex.Message;
            return;
        }

        foreach (var row in movableRows)
        {
            if (row.IsUnread) AdjustFolderUnread(row.Model.FolderId, -1);
            Messages.Remove(row);
        }

        SelectedRow = null;
        Detail = null;
        IsEmpty = Messages.Count == 0;
        ClearBatchSelection();
        StatusText = $"已在本地移动 {movableRows.Count} 封到「{target.Title}」，正在后台同步服务器…";

        await RefreshLocalTotalAsync();
        await ReloadAccountsAsync();

        // 后台回写：不阻塞界面，回来后再刷新待同步角标与状态栏
        _ = PushBatchMoveInBackgroundAsync(handle);
    }

    /// <summary>后台把整批移动回写服务器；只更新状态栏与计数，不再动列表。</summary>
    private async Task PushBatchMoveInBackgroundAsync(MailBatchMoveHandle handle)
    {
        try
        {
            var result = await Task.Run(() => _mailActions.PushMoveToFolderBatchAsync(handle));
            var pending = result.Total - result.RemoteConfirmed;

            await OnUiThreadAsync(async () =>
            {
                await RefreshPendingStateAsync();
                await ReloadAccountsAsync();
                StatusText = pending <= 0
                    ? $"已移动 {result.Total} 封到「{handle.Destination.DisplayName}」"
                    : $"已在本地移动 {result.Total} 封到「{handle.Destination.DisplayName}」，{pending} 项等待同步服务器";
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "后台批量移动回写失败");
            await OnUiThreadAsync(() =>
            {
                StatusText = $"批量移动已本地生效，服务器同步失败（稍后自动重试）：{ex.Message}";
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>把动作切回 UI 线程执行（后台任务里刷新绑定必须走这里）。</summary>
    private static async Task OnUiThreadAsync(Func<Task> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }

        var operation = dispatcher.InvokeAsync(action);
        await operation.Task.Unwrap().ConfigureAwait(true);
    }

    // ---------------- 邮件操作 ----------------

    /// <summary>切换选中邮件的已读 / 未读状态。</summary>
    [RelayCommand]
    private Task ToggleReadAsync()
        => SelectedRow is { } row ? SetReadStateAsync(row, row.IsUnread) : Task.CompletedTask;

    [RelayCommand]
    private Task MarkSelectedReadAsync()
        => SelectedRow is { } row ? SetReadStateAsync(row, read: true) : Task.CompletedTask;

    [RelayCommand]
    private Task MarkSelectedUnreadAsync()
        => SelectedRow is { } row ? SetReadStateAsync(row, read: false) : Task.CompletedTask;

    /// <summary>把当前视图（文件夹 / 搜索 / 筛选）内所有未读邮件标为已读，跨分页生效。</summary>
    [RelayCommand]
    private async Task MarkAllReadAsync()
    {
        try
        {
            var result = await _mailActions.MarkAllSeenAsync(BuildQuery(0));
            var targets = result.Targets;
            if (targets.Count == 0)
            {
                StatusText = "当前列表没有未读邮件";
                return;
            }

            var byId = targets.ToDictionary(t => t.Id);
            foreach (var row in Messages)
            {
                if (!byId.TryGetValue(row.Model.Id, out var target)) continue;
                row.Model.Flags = target.Flags;
                row.IsUnread = false;
            }

            foreach (var group in targets.GroupBy(t => t.FolderId))
            {
                AdjustFolderUnread(group.Key, -group.Count());
            }

            StatusText = result.RemoteConfirmed
                ? $"已将 {targets.Count} 封标记为已读"
                : $"已在本地标记 {targets.Count} 封为已读，等待同步服务器";
            OnPropertyChanged(nameof(ReadToggleText));
            OnPropertyChanged(nameof(IsSelectedStarred));
            OnPropertyChanged(nameof(IsSelectedUnread));
            await RefreshPendingStateAsync();
            await RefreshLocalTotalAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量标记已读失败");
            StatusText = "标记已读失败：" + ex.Message;
        }
    }

    /// <summary>由 ListBox 的多选状态同步当前页批量选择，不影响阅读窗格的单一详情选中。</summary>
    public void UpdateBatchSelection(IEnumerable<MailRow> rows)
    {
        var selected = rows.Select(r => r.Id).ToHashSet();
        foreach (var row in Messages) row.IsBatchSelected = selected.Contains(row.Id);
        BatchSelectedCount = selected.Count;
    }

    /// <summary>按当前选择向前/向后导航；仅未读模式跳过已读邮件。</summary>
    public void SelectRelative(int direction, bool unreadOnly)
    {
        if (Messages.Count == 0 || direction == 0) return;
        var start = SelectedRow is null ? (direction > 0 ? -1 : Messages.Count) : Messages.IndexOf(SelectedRow);
        for (var offset = 1; offset <= Messages.Count; offset++)
        {
            var index = (start + direction * offset + Messages.Count) % Messages.Count;
            var candidate = Messages[index];
            if (!unreadOnly || candidate.IsUnread)
            {
                SelectedRow = candidate;
                return;
            }
        }
    }

    [RelayCommand]
    private void ToggleBatchSelection()
    {
        if (SelectedRow is not { } row) return;
        row.IsBatchSelected = !row.IsBatchSelected;
        BatchSelectedCount = Messages.Count(item => item.IsBatchSelected);
    }

    [RelayCommand]
    private void SelectCurrentPage()
    {
        foreach (var row in Messages) row.IsBatchSelected = true;
        BatchSelectedCount = Messages.Count;
    }

    [RelayCommand]
    private void ClearBatchSelection()
    {
        foreach (var row in Messages) row.IsBatchSelected = false;
        BatchSelectedCount = 0;
    }

    [RelayCommand]
    private async Task ToggleStarAsync()
    {
        if (SelectedRow is not { } row) return;

        var starred = !row.IsStarred;

        // 本地优先：先立刻更新列表状态，再后台回写服务器（网络往返不能卡住 UI）
        row.IsStarred = starred;
        row.Model.Flags = starred
            ? row.Model.Flags | MessageFlags.Flagged
            : row.Model.Flags & ~MessageFlags.Flagged;

        // 工具条上的星标按钮据此换成金色加粗
        OnPropertyChanged(nameof(IsSelectedStarred));
        StatusText = starred ? "已加星标" : "已取消星标";

        var confirmed = await _mailActions.SetStarredAsync(row.Model, starred);

        StatusText = confirmed
            ? (starred ? "已加星标" : "已取消星标")
            : "已在本地更新星标，等待同步服务器";
        await RefreshPendingStateAsync();
    }

    [RelayCommand]
    private Task DeleteMessageAsync() => MoveSelectedAsync(FolderKind.Trash, "已移至垃圾箱");

    [RelayCommand]
    private Task ArchiveAsync() => MoveSelectedAsync(FolderKind.Archive, "已归档");

    [RelayCommand]
    private Task MarkAsJunkAsync() => MoveSelectedAsync(FolderKind.Junk, "已标记为垃圾邮件");

    [RelayCommand]
    private Task RestoreToInboxAsync() => MoveSelectedAsync(FolderKind.Inbox, "已恢复至收件箱");

    /// <summary>把当前（或批量选中的）邮件发件人加入本地黑名单，并把这些邮件移入垃圾箱。</summary>
    [RelayCommand]
    private async Task BlockSenderAsync()
    {
        var added = await SaveSelectedSendersAsync(isBlacklist: true);
        if (added == 0) return;

        await MoveSelectedAsync(FolderKind.Junk, "已按黑名单标记为垃圾邮件");
    }

    /// <summary>把当前（或批量选中的）邮件发件人加入本地白名单，之后新邮件不会被判为垃圾邮件。</summary>
    [RelayCommand]
    private Task AllowSenderAsync() => SaveSelectedSendersAsync(isBlacklist: false);

    /// <summary>保存选中邮件的发件人名单，返回成功条数。</summary>
    private async Task<int> SaveSelectedSendersAsync(bool isBlacklist)
    {
        var rows = HasBatchSelection
            ? Messages.Where(item => item.IsBatchSelected).ToList()
            : SelectedRow is { } selected ? new List<MailRow> { selected } : new List<MailRow>();
        if (rows.Count == 0) return 0;

        var senders = rows
            .Select(row => row.Model.FromEmail?.Trim())
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (senders.Count == 0)
        {
            StatusText = "这封邮件没有可用的发件人地址";
            return 0;
        }

        var added = 0;
        var lastMessage = "";
        foreach (var sender in senders)
        {
            var result = await _senderFilters.SaveAsync(sender, isBlacklist);
            lastMessage = result.Message;
            if (result.Success) added++;

            if (result.Success)
            {
                _logger.LogInformation("发件人 {Sender} 已加入{Kind}", sender, isBlacklist ? "黑名单" : "白名单");
            }
            else
            {
                _logger.LogWarning("发件人 {Sender} 加入名单失败：{Message}", sender, result.Message);
            }
        }

        if (!isBlacklist)
        {
            StatusText = added > 0
                ? $"已加入白名单 {added} 个发件人，后续邮件不会被判为垃圾邮件"
                : lastMessage;
        }

        return added;
    }

    private async Task MoveSelectedAsync(FolderKind destination, string actionText)
    {
        var rows = HasBatchSelection
            ? Messages.Where(item => item.IsBatchSelected).ToList()
            : SelectedRow is { } selected ? new List<MailRow> { selected } : new List<MailRow>();
        if (rows.Count == 0) return;

        var pending = 0;
        foreach (var row in rows)
        {
            var result = await _mailActions.MoveAsync(row.Model, destination);
            if (!result.RemoteConfirmed)
            {
                pending++;
                _lastDeleteOperationId = result.OperationId;
            }
            if (row.IsUnread) AdjustFolderUnread(row.Model.FolderId, -1);
            Messages.Remove(row);
        }

        SelectedRow = null;
        Detail = null;
        IsEmpty = Messages.Count == 0;
        CanUndoDelete = destination == FolderKind.Trash && pending > 0;
        ClearBatchSelection();
        await RefreshPendingStateAsync();
        await RefreshLocalTotalAsync();
        StatusText = pending == 0
            ? $"{actionText} {rows.Count} 封"
            : $"已在本地{actionText} {rows.Count} 封，{pending} 项等待同步服务器";
    }

    [RelayCommand]
    private async Task BatchMarkReadAsync() => await SetBatchReadStateAsync(read: true);

    [RelayCommand]
    private async Task BatchMarkUnreadAsync() => await SetBatchReadStateAsync(read: false);

    [RelayCommand]
    private async Task BatchToggleStarAsync()
    {
        var rows = Messages.Where(row => row.IsBatchSelected).ToList();
        if (rows.Count == 0) return;
        var target = rows.Any(row => !row.IsStarred);

        // 本地优先：整批先即时变色，再逐封回写服务器（N 次网络往返不再阻塞界面）
        foreach (var row in rows)
        {
            row.IsStarred = target;
            row.Model.Flags = target
                ? row.Model.Flags | MessageFlags.Flagged
                : row.Model.Flags & ~MessageFlags.Flagged;
        }
        StatusText = target ? $"已为 {rows.Count} 封邮件加星标" : $"已取消 {rows.Count} 封邮件星标";

        var confirmed = true;
        foreach (var row in rows)
        {
            confirmed &= await _mailActions.SetStarredAsync(row.Model, target);
        }

        if (!confirmed) StatusText = "部分星标操作已在本地更新，等待同步服务器";
        await RefreshPendingStateAsync();
    }

    private async Task SetBatchReadStateAsync(bool read)
    {
        var rows = Messages.Where(row => row.IsBatchSelected).ToList();
        if (rows.Count == 0) return;
        foreach (var row in rows.Where(row => row.IsUnread == !read))
        {
            await SetReadStateAsync(row, read, showStatus: false);
        }
        StatusText = read ? $"已将 {rows.Count} 封标为已读" : $"已将 {rows.Count} 封标为未读";
    }

    /// <summary>
    /// 状态栏「待同步 N 项」的明细：按操作类型 + 目标文件夹聚合成几行，
    /// 用户能看出卡在哪、什么时候开始卡的，并可直接重试（重试时本地已生效的项会自动结案）。
    /// </summary>
    [RelayCommand]
    private async Task ShowPendingSyncAsync()
    {
        var summary = await _mailActions.GetPendingSummaryAsync();
        await RefreshPendingStateAsync();

        if (summary.Count == 0)
        {
            StatusText = "没有需要同步的操作";
            return;
        }

        var total = summary.Sum(item => item.Count);
        var detail = string.Join(
            Environment.NewLine,
            summary.Select(item =>
            {
                var line = $"· {item.Label}：{item.Count} 项（最早 {item.Earliest.ToLocalTime():MM-dd HH:mm}，"
                    + $"最近 {item.Latest.ToLocalTime():MM-dd HH:mm}）";
                return string.IsNullOrWhiteSpace(item.LastError)
                    ? line
                    : line + Environment.NewLine + "    " + item.LastError;
            }));

        var retry = _dialogs.Confirm(
            "待同步明细",
            $"共 {total} 项本地已生效、服务器尚未确认的操作。点「立即重试」重新回写一次"
                + "（本地已经生效的项会自动结案，不会再无限重试）。",
            "立即重试",
            detail,
            DialogIcon.Question);

        if (retry) await RetryPendingAsync();
    }

    /// <summary>重试所有未同步成功的本地写操作。</summary>
    [RelayCommand]
    private async Task RetryPendingAsync()
    {
        try
        {
            var result = await _mailActions.FlushPendingAsync();
            await RefreshPendingStateAsync();
            await ReloadMessagesAsync();
            await RefreshLocalTotalAsync();

            StatusText = result.Failed > 0
                ? $"仍有 {result.Failed} 项操作未同步，请检查网络后重试"
                : result.Succeeded == 0
                    ? "没有需要同步的操作"
                    : $"已同步 {result.Succeeded} 项操作";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重试待同步操作失败");
            StatusText = "重试失败：" + ex.Message;
        }
    }

    /// <summary>撤销最近一次尚未被服务器确认的删除。</summary>
    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        if (_lastDeleteOperationId == 0) return;

        var restored = await _mailActions.UndoDeleteAsync(_lastDeleteOperationId);
        if (!restored)
        {
            StatusText = "撤销失败：删除已被服务器确认";
            CanUndoDelete = false;
            _lastDeleteOperationId = 0;
            return;
        }

        _lastDeleteOperationId = 0;
        CanUndoDelete = false;
        await RefreshPendingStateAsync();
        await SelectCurrentViewAsync();
        await RefreshLocalTotalAsync();
        StatusText = "已撤销删除";
    }

    // 附件操作统一交给 AttachmentActionService（独立阅读窗口共用同一实现），这里只负责把提示写进状态栏。

    /// <summary>附件双击：优先内置预览（受设置控制），不支持或已关闭时回退到系统程序。</summary>
    [RelayCommand]
    private void PreviewAttachment(AttachmentInfo? attachment) => ApplyAttachmentStatus(_attachments.Preview(attachment));

    /// <summary>右键「复制」/ Ctrl+C：把附件文件放进剪贴板。</summary>
    [RelayCommand]
    private void CopyAttachment(AttachmentInfo? attachment) => ApplyAttachmentStatus(_attachments.Copy(attachment));

    [RelayCommand]
    private void OpenAttachment(string? path) => ApplyAttachmentStatus(_attachments.Open(path));

    /// <summary>「仅收取正文」账户的附件按需下载：拉完整封邮件把附件落盘，然后刷新阅读区。</summary>
    [RelayCommand]
    private async Task DownloadAttachmentAsync(AttachmentInfo? attachment)
    {
        StatusText = "正在下载附件…";
        ApplyAttachmentStatus(await _attachments.DownloadAsync(attachment));

        if (attachment is { EmailId: > 0 } file && Detail?.Id == file.EmailId)
        {
            Detail = await _messages.GetDetailAsync(file.EmailId);
        }
    }

    /// <summary>附件另存为：把已下载到本地的副本复制到用户指定位置。</summary>
    [RelayCommand]
    private void SaveAttachment(AttachmentInfo? attachment) => ApplyAttachmentStatus(_attachments.Save(attachment));

    /// <summary>在资源管理器中定位附件文件。</summary>
    [RelayCommand]
    private void RevealAttachment(AttachmentInfo? attachment) => ApplyAttachmentStatus(_attachments.Reveal(attachment));

    /// <summary>把当前邮件的全部非内嵌附件保存到同一目录（重名自动加序号）。</summary>
    [RelayCommand]
    private void SaveAllAttachments()
        => ApplyAttachmentStatus(_attachments.SaveAll(Detail?.Attachments ?? new List<AttachmentInfo>()));

    /// <summary>往来邮件：直接移至垃圾箱（本地先落库 + 入待同步队列，可在状态栏撤销）。</summary>
    [RelayCommand]
    private async Task DeleteCorrespondenceAsync(CorrespondenceItem? item)
    {
        if (item is null) return;

        var detail = await _messages.GetDetailAsync(item.Id);
        if (detail is null)
        {
            StatusText = "邮件已不存在，可能已被删除";
            return;
        }

        await _mailActions.DeleteAsync(detail);
        CanUndoDelete = true;
        StatusText = "已移至垃圾箱";
        await RefreshPendingStateAsync();
        await SelectCurrentViewAsync();
    }

    /// <summary>发件人附件：打开该附件所属的那封邮件（在列表中定位并阅读）。</summary>
    [RelayCommand]
    private Task OpenSenderAttachmentMailAsync(SenderAttachmentItem? item)
        => item is null ? Task.CompletedTask : OpenMessageByIdAsync(item.MessageId);

    /// <summary>发件人附件：打开 Windows 原生「打开方式」选择框。</summary>
    [RelayCommand]
    private void OpenPathWith(string? path)
    {
        if (path is not { Length: > 0 } file || !File.Exists(file)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "选择打开方式失败：{Path}", file);
            StatusText = "无法选择打开方式：" + ex.Message;
        }
    }

    /// <summary>发件人附件：另存为。</summary>
    [RelayCommand]
    private void SavePathAs(string? path)
    {
        if (path is not { Length: > 0 } file || !File.Exists(file)) return;

        var dialog = new SaveFileDialog
        {
            Title = "另存附件",
            FileName = Path.GetFileName(file),
            Filter = "所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.Copy(file, dialog.FileName, overwrite: true);
            StatusText = "附件已保存：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "另存附件失败：{Path}", file);
            StatusText = "保存附件失败：" + ex.Message;
        }
    }

    /// <summary>发件人附件：在资源管理器中定位。</summary>
    [RelayCommand]
    private void RevealPath(string? path)
    {
        if (path is not { Length: > 0 } file || !File.Exists(file)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "定位附件失败：{Path}", file);
            StatusText = "无法打开附件所在目录：" + ex.Message;
        }
    }

    /// <summary>附件操作只在需要反馈时才改写状态栏，取消对话框等情况保持原提示。</summary>
    private void ApplyAttachmentStatus(string? message)
    {
        if (!string.IsNullOrEmpty(message)) StatusText = message;
    }

    // ---------------- 撰写 / 回复 / 转发（FR-07 / FR-08） ----------------

    /// <summary>写邮件；已在阅读邮件时默认沿用该邮件所属账户。</summary>
    [RelayCommand]
    private void Compose()
    {
        var accountId = Detail?.AccountId
            ?? SelectedRow?.Model.AccountId
            ?? Accounts.FirstOrDefault()?.Id;

        _dialogs.ShowCompose(new ComposeRequest { AccountId = accountId });
    }

    /// <summary>当前列表是否停在草稿箱（保存/删除草稿后要刷新它）。</summary>
    private bool IsDraftsView
        => _currentQuickView?.ScopeKind == FolderKind.Drafts
           || _currentFolder?.Kind == FolderKind.Drafts;

    /// <summary>
    /// 回复 / 回复全部 / 转发是否可用：草稿还没发出去，不能回复或转发，
    /// 只能双击草稿继续编辑（或直接删除）。
    /// </summary>
    public bool CanReplyOrForward
        => HasSelection && MailActionGuards.CanReplyOrForward(Detail, SelectedRow?.Model);

    /// <summary>
    /// 双击草稿箱里的草稿：用写信窗口打开继续编辑（正文、收件人、主题都带过去），
    /// 再保存会覆盖同一封草稿，发送成功后这封草稿会被删掉。
    /// </summary>
    public async Task OpenDraftAsync(long emailId)
    {
        var detail = await _messages.GetDetailAsync(emailId);
        if (detail is null)
        {
            StatusText = "草稿已不存在，可能已在其它设备删除";
            return;
        }

        var body = string.IsNullOrWhiteSpace(detail.BodyHtml) ? detail.BodyText : detail.BodyHtml;

        _dialogs.ShowCompose(new ComposeRequest
        {
            Mode = ComposeMode.New,
            AccountId = detail.AccountId,
            To = string.Join("; ", detail.To.Select(a => a.Display)),
            Cc = string.Join("; ", detail.Cc.Select(a => a.Display)),
            Bcc = string.Join("; ", detail.Bcc.Select(a => a.Display)),
            Subject = detail.Subject,
            Body = body,
            References = detail.References.Count > 0 ? string.Join(' ', detail.References) : null,
            DraftEmailId = emailId,
            Attachments = detail.Attachments
                .Where(a => a.IsDownloaded)
                .Select(a => a.LocalPath!)
                .ToArray(),
        });

        // 关窗后：可能已发送（草稿被删）或又存了一次，草稿箱列表需要跟上
        if (IsDraftsView) await SelectCurrentViewAsync();
    }

    [RelayCommand]
    private Task ReplyAsync() => OpenComposeAsync(ComposeMode.Reply);

    [RelayCommand]
    private Task ReplyAllAsync() => OpenComposeAsync(ComposeMode.ReplyAll);

    [RelayCommand]
    private Task ForwardAsync() => OpenComposeAsync(ComposeMode.Forward);

    /// <summary>
    /// 作为附件转发：把原邮件重建成 .eml 挂到新邮件的附件里（和 Outlook / Foxmail 的「作为附件转发」一致），
    /// 收件人拿到的是一整封原邮件，而不是被引用的正文。
    /// </summary>
    [RelayCommand]
    private async Task ForwardAsAttachmentAsync()
    {
        var detail = Detail;
        if (detail is null && SelectedRow is { } row)
        {
            detail = await _messages.GetDetailAsync(row.Id);
        }

        if (detail is null)
        {
            StatusText = "请先选择一封邮件";
            return;
        }

        if (!detail.HasBody)
        {
            StatusText = "正文尚未下载：先打开这封邮件，再用「作为附件转发」";
            return;
        }

        try
        {
            var path = await Task.Run(() => MailEmlExporter.ExportAsync(detail, _emlDirectory));
            if (path is null)
            {
                StatusText = "正文尚未下载：先打开这封邮件，再用「作为附件转发」";
                return;
            }

            _dialogs.ShowCompose(new ComposeRequest
            {
                AccountId = detail.AccountId,
                Subject = ComposeRequestFactory.SubjectPrefix(detail.Subject, "Fwd:"),
                Body = string.Empty,
                Attachments = new[] { path }
            });

            StatusText = $"已把原邮件作为附件转发：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "作为附件转发失败：{EmailId}", detail.Id);
            StatusText = "作为附件转发失败：" + ex.Message;
        }
    }

    /// <summary>打开撰写窗。详情尚未加载时按选中行补取一次，右键菜单也能直接回复。</summary>
    private async Task OpenComposeAsync(ComposeMode mode)
    {
        var detail = Detail;
        if (detail is null && SelectedRow is { } row)
        {
            detail = await _messages.GetDetailAsync(row.Id);
        }

        if (detail is null)
        {
            StatusText = "请先选择一封邮件";
            return;
        }

        var self = Accounts.FirstOrDefault(a => a.Id == detail.AccountId)?.Model.Email ?? string.Empty;
        var request = ComposeRequestFactory.ForReply(
            detail,
            mode,
            self,
            path => ComposeRequestFactory.ReadLocalImage(path, _emlDirectory));

        // 草稿还没有发出去，回复/转发没有意义：双击草稿继续编辑即可
        if (request is null)
        {
            StatusText = "草稿邮件不能回复或转发，双击草稿可以继续编辑";
            return;
        }

        _dialogs.ShowCompose(request);
    }

    // ---------------- 设置 / 发件箱 ----------------

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var freightBefore = FreightFlags;

        _dialogs.ShowSettings();

        // 设置里可能关掉「标记陌生发件人」：回到主界面立即把角标收起来（不清库）
        ApplyStrangerBadgeVisibility();

        // 设置中可能变更账户、主题、字号、正文渲染、列表参数与标签快捷键，返回后整体刷新
        await ReloadSidebarTagsAsync();
        IsDark = _theme.IsDark;
        UseHtmlRendering = _settings.Current.HtmlRendering;
        OnPropertyChanged(nameof(BodyFontSize));
        OnPropertyChanged(nameof(BodyHtmlSource));
        RefreshBodyDocument();

        // 货代开关：关闭的项立即清空看板并隐藏入口；重新开启的项清掉补扫进度、重新排队抽取
        var freightAfter = FreightFlags;
        RaiseFreightVisibility();
        ClearDisabledFreightBoards();

        var reEnabled = (!freightBefore.References && freightAfter.References)
            || (!freightBefore.MailTypes && freightAfter.MailTypes)
            || (!freightBefore.Deadlines && freightAfter.Deadlines);

        if (reEnabled)
        {
            try
            {
                await _referenceService.ClearScanProgressAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "重置货代补扫进度失败");
            }
        }

        await ReloadAccountsAsync();
        await SelectCurrentViewAsync();
        await RefreshLocalTotalAsync();

        // 仿冒域名：容差 / 开关可能刚改过，账户域名也可能变了 —— 对当前页与阅读区重算一次
        ApplyPhishingVisibility();

        if (freightAfter.AnyRecognition)
        {
            // 补扫本身丢到线程池，回到 UI 线程再刷看板（ObservableCollection 不能跨线程改）
            await Task.Run(() => _referenceService.IndexPendingAsync(500, 10));
            await LoadBusinessRefsAsync();
            await LoadDeadlinesAsync();
        }
    }

    [RelayCommand]
    private async Task OpenOutboxAsync()
    {
        _dialogs.ShowOutbox();
        await RefreshLocalTotalAsync();
    }

    /// <summary>通讯录（地址簿）：搜索、编辑、写信与 vCard/CSV 导入导出。</summary>
    [RelayCommand]
    private void OpenAddressBook() => _dialogs.ShowAddressBook();

    /// <summary>按当前选中的快捷视图 / 文件夹重新拉取列表（不改变选中态）。</summary>
    private Task SelectCurrentViewAsync()
        => _currentFolder is not null ? SelectFolderAsync(_currentFolder) : SelectQuickViewAsync(_currentQuickView);

    // ---------------- 同步 ----------------

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsSyncing) return;

        IsSyncing = true;
        IsSyncIndeterminate = true;
        SyncPercent = 0;
        StatusText = "正在增量同步…";

        try
        {
            // 先重试未同步的本地写操作，再拉取服务器状态：
            // 避免同步校准把刚刚本地修改的已读 / 星标 / 删除又覆盖回去。
            var flushed = await _mailActions.FlushPendingAsync();
            StatusText = await _syncCoordinator.SyncAsync();
            if (flushed.Failed > 0)
            {
                StatusText += $" · {flushed.Failed} 项本地操作待重试";
            }

            await ReloadAccountsAsync();
            await ReloadMessagesAsync();
            await RefreshLocalTotalAsync();
            await RefreshStorageAsync();
            await RefreshPendingStateAsync();

            // 新邮件入库后补扫业务单号并刷新侧栏，不阻塞同步结果展示
            FireAndForget.Run(WarmReferenceIndexAsync, "预建业务单号索引");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步后刷新界面失败");
            StatusText = "刷新失败：" + ex.Message;
        }
        finally
        {
            IsSyncing = false;
            IsSyncIndeterminate = false;
            SyncPercent = 0;
        }
    }

    /// <summary>账户右键「收取」的可用性：停用、配置不完整、正在同步时都不给点。</summary>
    private bool CanSyncAccount(AccountNode? node) => node is { CanSync: true } && !IsSyncing;

    /// <summary>
    /// 只同步某一个账户（侧栏账户右键「收取」）：多账户时不必等整轮全量同步。
    /// 与全账户同步共用同一道同步门，后台轮询正在跑时会排队等它结束。
    /// </summary>
    /// <remarks>停用账户、配置不完整、正在同步时菜单项自动置灰。</remarks>
    [RelayCommand(CanExecute = nameof(CanSyncAccount))]
    private async Task SyncAccountAsync(AccountNode? node)
    {
        if (node is null) return;

        if (IsSyncing)
        {
            StatusText = "正在同步中，请稍后再试";
            return;
        }

        IsSyncing = true;
        IsSyncIndeterminate = true;
        SyncPercent = 0;
        StatusText = $"正在同步「{node.Title}」…";

        try
        {
            // 先把本地待同步的写操作推上去，避免这次校准把它们覆盖回去
            var flushed = await _mailActions.FlushPendingAsync();
            var outcome = await _syncCoordinator.SyncAccountAsync(node.Id);

            StatusText = outcome.Success
                ? $"「{node.Title}」同步完成 · {outcome.FoldersSynced} 个文件夹 · 新增 {outcome.NewMessages} 封"
                : $"「{node.Title}」同步失败：{outcome.Error}";
            if (flushed.Failed > 0) StatusText += $" · {flushed.Failed} 项本地操作待重试";

            await ReloadAccountsAsync();
            await ReloadMessagesAsync();
            await RefreshLocalTotalAsync();
            await RefreshStorageAsync();
            await RefreshPendingStateAsync();

            // 新邮件入库后补扫业务单号并刷新侧栏，不阻塞状态栏结果
            FireAndForget.Run(WarmReferenceIndexAsync, "预建业务单号索引");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步账户失败：{AccountId}", node.Id);
            StatusText = $"「{node.Title}」同步失败：" + ex.Message;
        }
        finally
        {
            IsSyncing = false;
            IsSyncIndeterminate = false;
            SyncPercent = 0;
        }
    }

    private void OnSyncProgress(object? sender, SyncProgress progress)
    {
        OnUi(() =>
        {
            StatusText = progress.Text;
            IsSyncIndeterminate = progress.IsIndeterminate;
            SyncPercent = progress.Percent;
        });
    }

    /// <summary>
    /// 后台同步（定时轮询 / IDLE 推送）结束：刷新侧栏未读数，并把新邮件并进当前列表。
    /// 以前这里只更新状态栏，邮件其实已经入库但界面上看不到，用户会以为「根本没同步」。
    /// </summary>
    private void OnBackgroundSyncCompleted(object? sender, IReadOnlyList<SyncOutcome> outcomes)
    {
        var hasNew = outcomes.Any(outcome => outcome.Success && outcome.NewMessages > 0);
        var hasFlagChange = outcomes.Any(outcome => outcome.Success && outcome.UpdatedFlags > 0);
        if (!hasNew && !hasFlagChange) return;

        OnUi(() => _ = ApplyBackgroundSyncAsync(hasNew));
    }

    /// <summary>后台投递成功：写邮件窗口早就关了，这里在状态栏补一句结果。</summary>
    private void OnOutboxItemSent(object? sender, OutboxItem item)
        => OnUi(() => StatusText = $"「{item.SubjectDisplay}」已发送");

    /// <summary>后台投递失败：说明还在发件箱里、可重试，避免用户以为信已经发出去了。</summary>
    private void OnOutboxItemFailed(object? sender, OutboxItem item)
        => OnUi(() => StatusText = $"「{item.SubjectDisplay}」发送失败：{item.LastError}（已保留在发件箱，可重试）");

    private async Task ApplyBackgroundSyncAsync(bool hasNewMail)
    {
        try
        {
            await ReloadAccountsAsync();
            await RefreshLocalTotalAsync();
            if (hasNewMail) await MergeNewMessagesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "后台同步后刷新界面失败");
        }
    }

    /// <summary>
    /// 把后台同步新增的邮件并进当前列表：不重排、不跳页、不动选中行。
    /// 翻到第 2 页、搜索结果、会话视图、单封定位这些场景一律不动列表，避免打断正在阅读的用户。
    /// </summary>
    private async Task MergeNewMessagesAsync()
    {
        if (IsSearchResults || CurrentPage > 1 || ConversationMode || _pinnedEmailId is not null) return;
        if (Messages.Count == 0) return;

        var page = await Task.Run(() => _messages.QueryAsync(BuildQuery(0)));
        var rows = page.Items.Select(summary => new MailRow(summary, avatars: _avatars)).ToList();
        if (rows.Count == 0) return;

        var existingIds = Messages.Select(row => row.Id).ToHashSet();
        var queriedIds = rows.Select(row => row.Id).ToHashSet();
        var missing = rows.Where(row => !existingIds.Contains(row.Id)).ToList();
        var removed = Messages.Where(row => !queriedIds.Contains(row.Id)).ToList();

        if (missing.Count == 0 && removed.Count == 0)
        {
            TotalCount = page.TotalCount;
            return;
        }

        var selectedId = SelectedRow?.Id;

        foreach (var row in removed) Messages.Remove(row);
        foreach (var row in missing) InsertRowInOrder(row, rows);

        // 保持「一页多少封」不变：多了就从尾部裁掉，避免列表越滚越长
        while (Messages.Count > PageSize) Messages.RemoveAt(Messages.Count - 1);

        TotalCount = page.TotalCount;
        ListSubtitle = (IsSearchResults ? (IsSearchScopeAllMail ? "所有邮件 · " : "当前文件夹 · ") : string.Empty)
                       + $"共 {TotalCount} 封 · 第 {CurrentPage}/{TotalPages} 页";

        if (selectedId is { } id && Messages.All(row => row.Id != id))
        {
            // 选中的那封已经不在当前视图（比如被规则挪走了）：退回最后一行，避免阅读区留空
            SelectedRow = Messages.Count > 0 ? Messages[^1] : null;
        }
    }

    /// <summary>按查询顺序把新行插到正确位置（列表与查询结果都是「新邮件在前」）。</summary>
    private void InsertRowInOrder(MailRow row, IReadOnlyList<MailRow> ordered)
    {
        var target = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Id != row.Id) continue;
            target = i;
            break;
        }

        var insertAt = Messages.Count;
        for (var i = 0; i < Messages.Count; i++)
        {
            var index = -1;
            for (var j = 0; j < ordered.Count; j++)
            {
                if (ordered[j].Id != Messages[i].Id) continue;
                index = j;
                break;
            }

            if (index >= 0 && target >= 0 && index > target)
            {
                insertAt = i;
                break;
            }
        }

        Messages.Insert(insertAt, row);
    }

    // ---------------- 内部工具 ----------------

    private async Task ReloadAccountsAsync()
    {
        // 三次查询一次性丢给线程池：Microsoft.Data.Sqlite 的 *Async 底层同步执行，
        // 直接在界面线程 await 等于把这几条 SQL（2.9GB 库落在机械盘上时更明显）跑在界面线程。
        // 这个方法每轮后台同步（有新信 / 标志位变化）都会被调用，是「每分钟一卡」的常见来源。
        var (accounts, localCounts, folderMap) = await Task.Run(async () =>
        {
            var list = await _accounts.GetAllAsync();

            // 侧栏角标统一用本地口径（与列表一致）：服务器 STATUS 的未读数可能还算着
            // 「本地已经移走 / 删除、只是服务器还没 EXPUNGE」的邮件，会出现「0 封却挂 1」。
            var counts = await _messages.GetFolderCountsAsync();

            var map = new Dictionary<long, IReadOnlyList<MailFolder>>();
            foreach (var account in list)
            {
                map[account.Id] = await _folders.GetByAccountAsync(account.Id);
            }

            return (list, counts, map);
        });

        for (var i = Accounts.Count - 1; i >= 0; i--)
        {
            if (accounts.All(a => a.Id != Accounts[i].Id)) Accounts.RemoveAt(i);
        }

        foreach (var account in accounts)
        {
            var node = Accounts.FirstOrDefault(a => a.Id == account.Id);
            if (node is null)
            {
                node = new AccountNode(account, _avatars);
                Accounts.Add(node);
            }

            var folders = folderMap.TryGetValue(account.Id, out var cached)
                ? cached
                : Array.Empty<MailFolder>();
            // 账户被停用后，若当前正停在其文件夹上，自动回到统一收件箱，避免停留在不再更新的列表。
            if (!account.IsEnabled && _currentFolder is { } current && current.AccountId == account.Id)
            {
                _currentFolder = null;
                _currentQuickView = QuickViews.FirstOrDefault();
                if (_currentQuickView is { } quick)
                {
                    foreach (var item in QuickViews) item.IsSelected = ReferenceEquals(item, quick);
                    _suspendReload = true;
                    Filter = quick.Filter;
                    _suspendReload = false;
                    ListTitle = quick.Title;
                }
                FireAndForget.Run(() => ReloadMessagesAsync(), "刷新邮件列表");
            }

            node.Update(account);
            var existing = node.Folders.ToDictionary(f => f.Id);
            node.Folders.Clear();

            foreach (var folder in folders.OrderBy(SortKey).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            {
                // 角标取本地统计；本地一条都没有时归零，不在空文件夹上留幻觉数字
                if (localCounts.TryGetValue(folder.Id, out var local))
                {
                    folder.UnreadCount = local.Unread;
                    folder.TotalCount = local.Total;
                }
                else
                {
                    folder.UnreadCount = 0;
                    folder.TotalCount = 0;
                }

                if (existing.TryGetValue(folder.Id, out var folderNode))
                {
                    folderNode.Update(folder);
                    node.Folders.Add(folderNode);
                }
                else
                {
                    node.Folders.Add(new FolderNode(folder));
                }
            }

            node.RefreshUnread();
        }

        OnPropertyChanged(nameof(AccountSummary));

        // 账户域名变了（新增 / 换邮箱 / 停用），仿冒域名的基准也要跟着换
        RefreshOwnDomains();

        // 账户启停 / 配置补齐后，右键「收取」的可用状态要跟着刷新
        SyncAccountCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshLocalTotalAsync()
    {
        try
        {
            // 一次按文件夹分组的聚合顶原来两次全表 COUNT，且放到线程池：每封新邮件同步后都会走这里
            var counts = await Task.Run(() => _messages.GetFolderCountsAsync()).ConfigureAwait(true);
            LocalTotal = counts.Values.Sum(pair => pair.Total);
            LocalUnread = counts.Values.Sum(pair => pair.Unread);
            OnPropertyChanged(nameof(AccountSummary));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新本地邮件统计失败");
        }
    }

    /// <summary>刷新状态栏的待同步操作数量。</summary>
    private async Task RefreshPendingStateAsync()
    {
        try
        {
            // 仓储调用一律在线程池执行（SQLite 的 async 底层是同步的）
            PendingSyncCount = await Task.Run(() => _mailActions.GetPendingCountAsync());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取待同步操作数量失败");
        }
    }

    private static int SortKey(MailFolder folder) => folder.Kind switch
    {
        FolderKind.Inbox => 0,
        FolderKind.Drafts => 1,
        FolderKind.Sent => 2,
        FolderKind.Archive => 3,
        FolderKind.Junk => 4,
        FolderKind.Trash => 5,
        _ => 6
    };

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        // 后台线程只把动作排队，不同步等界面线程：同步进度每批都会触发，
        // 以前用 Invoke 会让 IMAP 线程在这里被界面拖住（界面忙 1 秒，同步就停 1 秒），
        // 界面线程若正等这条同步链就有死锁风险。
        dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }
}

