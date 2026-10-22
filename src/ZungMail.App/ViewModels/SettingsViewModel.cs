using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.Application.Services;
using ZungMail.App.Services;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using ZungMail.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ZungMail.App.ViewModels;

/// <summary>设置导航项。</summary>
public sealed record SettingsSection(string Key, string Title, string IconKey);

/// <summary>
/// 设置中心：通用偏好、账户管理（FR-01）、模板管理（FR-08）、数据导出（FR-13）。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] FontSizes = { "compact", "normal", "large" };

    /// <summary>「正文下载」下拉的取值（天）：0 = 只同步邮件头，全部按需下载。</summary>
    private static readonly int[] BodyPrefetchDayOptions =
    {
        0, 1, 3, 7, 30, 60, 182, AppSettings.BodyPrefetchAllDays
    };

    /// <summary>邮件列表每页封数的候选档位（与主界面 10~500 的兜底夹取保持一致）。</summary>
    private static readonly int[] ListPageSizeValues = { 20, 50, 100, 200, 300, 500 };

    /// <summary>折叠时「保留最近几层」的候选档位（与文案一一对应）。</summary>
    private static readonly int[] FoldKeepLayerValues = { 5, 6, 7, 8, 9, 10 };

    private readonly ISettingsStore _settings;
    private readonly IAccountRepository _accounts;
    private readonly ITemplateStore _templates;
    private readonly IDataExporter _exporter;
    private readonly IAppPaths _paths;
    private readonly IMessageRepository _messages;
    private readonly IMailSyncService _sync;
    private readonly IMailDiagnosticsService _diagnostics;
    private readonly IMailImportService _importer;
    private readonly IFolderRepository _folders;
    private readonly MailRuleService _rules;
    private readonly MailTagService _tags;
    private readonly SenderFilterService _senderFilters;
    private readonly ContactExchangeService _contactExchange;
    private readonly ClientLockService _clientLock;
    private readonly GlobalHotkeyService _hotkey;
    private readonly IStorageMonitor _storageMonitor;
    private readonly ICleanupService _cleanup;
    private readonly IStartupLaunchService _startupLaunch;
    private readonly ThemeManager _theme;
    private readonly IDialogService _dialogs;
    private readonly IDataDirectoryService _dataDirectories;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>窗口已关闭并释放：挡住排队中的预览任务与延迟回调。</summary>
    private bool _disposed;

    public SettingsViewModel(
        ISettingsStore settings,
        IAccountRepository accounts,
        ITemplateStore templates,
        IDataExporter exporter,
        IAppPaths paths,
        IMessageRepository messages,
        IMailSyncService sync,
        IMailDiagnosticsService diagnostics,
        IMailImportService importer,
        IFolderRepository folders,
        MailRuleService rules,
        MailTagService tags,
        SenderFilterService senderFilters,
        ContactExchangeService contactExchange,
        ClientLockService clientLock,
        GlobalHotkeyService hotkey,
        IStorageMonitor storageMonitor,
        ICleanupService cleanup,
        IStartupLaunchService startupLaunch,
        ThemeManager theme,
        IDialogService dialogs,
        IDataDirectoryService dataDirectories,
        UpdateCoordinator updateCoordinator,
        UpdateWindowService updateWindowService,
        IUpdateService updateService,
        IFeedbackService feedbackService,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _accounts = accounts;
        _templates = templates;
        _exporter = exporter;
        _paths = paths;
        _messages = messages;
        _sync = sync;
        _diagnostics = diagnostics;
        _importer = importer;
        _folders = folders;
        _rules = rules;
        _tags = tags;
        _senderFilters = senderFilters;
        _contactExchange = contactExchange;
        _clientLock = clientLock;
        _hotkey = hotkey;
        _storageMonitor = storageMonitor;
        _cleanup = cleanup;
        _startupLaunch = startupLaunch;
        _theme = theme;
        _dialogs = dialogs;
        _dataDirectories = dataDirectories;
        _logger = logger;

        InitializeUpdate(updateCoordinator, updateWindowService, updateService);
        InitializeFeedback(feedbackService);

        Sections = new List<SettingsSection>
        {
            new("general", "通用", "IcoGear"),
            new("accounts", "账户", "IcoUsers"),
            new("signature", "签名", "IcoCompose"),
            new("templates", "模板", "IcoTemplate"),
            new("rules", "规则", "IcoTemplate"),
            new("freight", "货代", "IcoCalendar"),
            new("filters", "反垃圾", "IcoJunk"),
            new("tags", "标签", "IcoArchive"),
            new("storage", "存储与清理", "IcoDatabase"),
            new("data", "数据", "IcoDatabase"),
            new("update", "升级", "IcoRefresh"),
            new("feedback", "反馈", "IcoQuestion"),
            new("about", "关于", "IcoInfo")
        };
        SelectedSection = Sections[0];

        RefreshDataDirectoryState();
        RefreshLogUsage();
    }

    public AppSettings Settings => _settings.Current;

    public IReadOnlyList<SettingsSection> Sections { get; }

    public IReadOnlyList<string> FontSizeOptions { get; } = new[] { "紧凑", "标准", "较大" };

    public ObservableCollection<Account> Accounts { get; } = new();

    /// <summary>账户页的富信息卡片（与 <see cref="Accounts"/> 同序，命令仍操作 <see cref="Account"/>）。</summary>
    public ObservableCollection<AccountCardItem> AccountCards { get; } = new();

    /// <summary>按账户缓存的本地邮件数 / 未读数，避免每次重绘卡片都查库。</summary>
    private readonly Dictionary<long, (int Total, int Unread)> _accountStats = new();

    /// <summary>签名页的账户条目（每个账户一条：是否启用 + 富文本签名）。</summary>
    public ObservableCollection<SignatureAccountItem> SignatureAccounts { get; } = new();

    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty] private SettingsSection _selectedSection = null!;
    [ObservableProperty] private Account? _selectedAccount;
    [ObservableProperty] private AccountCardItem? _selectedAccountCard;
    [ObservableProperty] private SignatureAccountItem? _selectedSignatureAccount;
    [ObservableProperty] private bool _signatureEnabled;
    [ObservableProperty] private string _signatureHtml = "";
    [ObservableProperty] private TemplateListItem? _selectedTemplate;
    [ObservableProperty] private string _templateName = "";
    [ObservableProperty] private string _templateSubject = "";
    [ObservableProperty] private string _templateBody = "";
    [ObservableProperty] private string _templateDescription = "";
    [ObservableProperty] private string _templateCategory = MailTemplate.DefaultCategory;
    [ObservableProperty] private TemplateViewMode _templateViewMode = TemplateViewMode.Split;
    [ObservableProperty] private string _templatePreviewDocument = "";
    [ObservableProperty] private string _templatePreviewSource = "";
    [ObservableProperty] private string _templatePreviewSubject = "";
    [ObservableProperty] private string _templateNameError = "";
    [ObservableProperty] private string _templateSubjectError = "";
    [ObservableProperty] private string _templateBodyError = "";
    [ObservableProperty] private string _templateHint = "";
    [ObservableProperty] private string _dataDirectory = "";
    [ObservableProperty] private string _storageSize = "计算中…";
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private StorageSnapshot? _storageSnapshot;

    // ---------------- 分区可见性 ----------------

    public bool IsGeneralSection => SelectedSection.Key == "general";
    public bool IsAccountsSection => SelectedSection.Key == "accounts";
    public bool IsSignatureSection => SelectedSection.Key == "signature";
    public bool IsTemplatesSection => SelectedSection.Key == "templates";
    public bool IsRulesSection => SelectedSection.Key == "rules";
    public bool IsFreightSection => SelectedSection.Key == "freight";
    public bool IsFiltersSection => SelectedSection.Key == "filters";
    public bool IsTagsSection => SelectedSection.Key == "tags";
    public bool IsStorageSection => SelectedSection.Key == "storage";
    public bool IsDataSection => SelectedSection.Key == "data";
    public bool IsAboutSection => SelectedSection.Key == "about";

    /// <summary>关于页展示的版本号：由 <see cref="AppVersionInfo"/> 统一提供（去源码链接后缀）。</summary>
    public string AppVersion => AppVersionInfo.Display;

    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsGeneralSection));
        OnPropertyChanged(nameof(IsAccountsSection));
        OnPropertyChanged(nameof(IsSignatureSection));
        OnPropertyChanged(nameof(IsTemplatesSection));
        OnPropertyChanged(nameof(IsRulesSection));
        OnPropertyChanged(nameof(IsFreightSection));
        OnPropertyChanged(nameof(IsFiltersSection));
        OnPropertyChanged(nameof(IsTagsSection));
        OnPropertyChanged(nameof(IsStorageSection));
        OnPropertyChanged(nameof(IsDataSection));
        OnPropertyChanged(nameof(IsUpdateSection));
        OnPropertyChanged(nameof(IsFeedbackSection));
        OnPropertyChanged(nameof(IsAboutSection));

        if (value.Key == "data") _ = RefreshStorageSizeAsync();
        if (value.Key == "storage") _ = RefreshStorageAsync();
        if (value.Key == "feedback") _ = PrepareFeedbackAsync();
        if (value.Key == "general") RefreshLogUsage();
        if (value.Key == "about") RefreshShortcutState();
        if (value.Key == "update")
        {
            OnPropertyChanged(nameof(UpdateAddressPreview));
            OnPropertyChanged(nameof(UpdateAddressIsError));
            OnPropertyChanged(nameof(UpdateStatusText));
            OnPropertyChanged(nameof(HasAvailableUpdate));
            OnPropertyChanged(nameof(UpdateActionText));
        }
        if (value.Key == "rules") _ = ReloadRulesAsync();
        if (value.Key == "filters") _ = ReloadSenderFiltersAsync();
        if (value.Key == "tags") _ = ReloadTagsAsync();
        if (value.Key == "templates") RefreshTemplatePreview();
    }

    // ---------------- 通用偏好 ----------------

    /// <summary>
    /// 主题选择：默认（中性灰）/ 淡蓝 / 淡粉 / 淡橙，选中即时预览，保存设置后持久化。
    /// 暗色模式已下线，历史配置里的 dark 会被 <see cref="ThemeManager.Normalize"/> 归一到默认亮色。
    /// </summary>
    public bool IsDefaultTheme
    {
        get => IsTheme(ThemeKeys.Default);
        set { if (value) SelectTheme(ThemeKeys.Default); }
    }

    public bool IsBlueTheme
    {
        get => IsTheme(ThemeKeys.Blue);
        set { if (value) SelectTheme(ThemeKeys.Blue); }
    }

    public bool IsPinkTheme
    {
        get => IsTheme(ThemeKeys.Pink);
        set { if (value) SelectTheme(ThemeKeys.Pink); }
    }

    public bool IsOrangeTheme
    {
        get => IsTheme(ThemeKeys.Orange);
        set { if (value) SelectTheme(ThemeKeys.Orange); }
    }

    private bool IsTheme(string key)
        => string.Equals(ThemeManager.Normalize(Settings.Theme), key, StringComparison.Ordinal);

    private void SelectTheme(string key)
    {
        if (IsTheme(key)) return;

        Settings.Theme = key;
        _theme.Apply(key);

        OnPropertyChanged(nameof(IsDefaultTheme));
        OnPropertyChanged(nameof(IsBlueTheme));
        OnPropertyChanged(nameof(IsPinkTheme));
        OnPropertyChanged(nameof(IsOrangeTheme));
        OnPropertyChanged(nameof(IsDarkPreview));
    }

    public int FontSizeIndex
    {
        get => Math.Max(0, Array.IndexOf(FontSizes, Settings.FontSize));
        set
        {
            var index = Math.Clamp(value, 0, FontSizes.Length - 1);
            Settings.FontSize = FontSizes[index];
            OnPropertyChanged();
        }
    }

    /// <summary>「正文下载」下拉文案：窗口内自动下正文与附件，窗口外按需下载。</summary>
    public IReadOnlyList<string> BodyPrefetchOptions { get; } = new[]
    {
        "仅邮件头（选中时下载）",
        "最近 1 天",
        "最近 3 天",
        "最近 7 天",
        "最近 30 天",
        "最近两个月",
        "最近半年",
        "全部邮件"
    };

    public int BodyPrefetchIndex
    {
        // 手工改过配置文件时落到最接近的档位，界面上不出现空白选项
        get => SettingsTier.NearestIndex(BodyPrefetchDayOptions, Settings.BodyPrefetchDays);
        set
        {
            var index = Math.Clamp(value, 0, BodyPrefetchDayOptions.Length - 1);
            Settings.BodyPrefetchDays = BodyPrefetchDayOptions[index];
            OnPropertyChanged();
        }
    }

    /// <summary>「折叠时保留最近几层」下拉文案（与 <see cref="FoldKeepLayerValues"/> 一一对应）。</summary>
    public IReadOnlyList<string> FoldKeepLayerOptions { get; } = new[]
    {
        "保留最近 5 层",
        "保留最近 6 层",
        "保留最近 7 层",
        "保留最近 8 层",
        "保留最近 9 层",
        "保留最近 10 层"
    };

    public int FoldKeepLayerIndex
    {
        get => SettingsTier.NearestIndex(FoldKeepLayerValues, Settings.FoldQuotedHistoryKeepLayers);
        set
        {
            var index = Math.Clamp(value, 0, FoldKeepLayerValues.Length - 1);
            Settings.FoldQuotedHistoryKeepLayers = FoldKeepLayerValues[index];
            OnPropertyChanged();
        }
    }

    /// <summary>折叠开关（包一层属性，好让下面的「保留层数」跟着启用 / 禁用）。</summary>
    public bool FoldQuotedHistoryEnabled
    {
        get => Settings.FoldQuotedHistory;
        set
        {
            if (Settings.FoldQuotedHistory == value) return;
            Settings.FoldQuotedHistory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FoldQuotedHistoryKeepEnabled));
        }
    }

    /// <summary>只有打开折叠，保留层数才有意义。</summary>
    public bool FoldQuotedHistoryKeepEnabled => Settings.FoldQuotedHistory;

    /// <summary>仿冒域名相似度容差：默认只差 1 个字符，可放宽到 2 个（更容易误报）。</summary>
    public IReadOnlyList<string> PhishingToleranceOptions { get; } = new[]
    {
        "差 1 个字符（默认）",
        "差 2 个字符（更宽松）"
    };

    public int PhishingToleranceIndex
    {
        get => PhishingDomainPolicy.NormalizeTolerance(Settings.PhishingDomainTolerance) - 1;
        set
        {
            Settings.PhishingDomainTolerance =
                PhishingDomainPolicy.NormalizeTolerance(Math.Clamp(value, 0, 1) + 1);
            OnPropertyChanged();
        }
    }

    /// <summary>邮件列表每页封数的下拉文案（与 <see cref="ListPageSizeValues"/> 一一对应）。</summary>
    public IReadOnlyList<string> ListPageSizeOptions { get; } = new[]
    {
        "20 封 / 页",
        "50 封 / 页",
        "100 封 / 页",
        "200 封 / 页",
        "300 封 / 页",
        "500 封 / 页"
    };

    /// <summary>每页封数：下拉只给常用档位；配置文件被手工改成别的值时落到最接近的档位。</summary>
    public int ListPageSizeIndex
    {
        get => SettingsTier.NearestIndex(ListPageSizeValues, Settings.ListPageSize);
        set
        {
            var index = Math.Clamp(value, 0, ListPageSizeValues.Length - 1);
            Settings.ListPageSize = ListPageSizeValues[index];
            OnPropertyChanged();
        }
    }

    // ---------------- 货代行业功能（总开关 + 4 个子项） ----------------

    /// <summary>总开关：关掉后四项一律按关闭处理，子项置灰（勾选状态保留）。</summary>
    public bool FreightFeaturesEnabled
    {
        get => Settings.FreightFeaturesEnabled;
        set
        {
            if (value == Settings.FreightFeaturesEnabled) return;
            Settings.FreightFeaturesEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFreightOptionsEnabled));
        }
    }

    /// <summary>子项是否可编辑（跟随总开关）。</summary>
    public bool IsFreightOptionsEnabled => Settings.FreightFeaturesEnabled;

    public bool FreightReferencesEnabled
    {
        get => Settings.FreightReferencesEnabled;
        set
        {
            if (value == Settings.FreightReferencesEnabled) return;
            Settings.FreightReferencesEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool FreightMailTypesEnabled
    {
        get => Settings.FreightMailTypesEnabled;
        set
        {
            if (value == Settings.FreightMailTypesEnabled) return;
            Settings.FreightMailTypesEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool FreightDeadlinesEnabled
    {
        get => Settings.FreightDeadlinesEnabled;
        set
        {
            if (value == Settings.FreightDeadlinesEnabled) return;
            Settings.FreightDeadlinesEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool FreightFollowUpsEnabled
    {
        get => Settings.FreightFollowUpsEnabled;
        set
        {
            if (value == Settings.FreightFollowUpsEnabled) return;
            Settings.FreightFollowUpsEnabled = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            _startupLaunch.SetEnabled(Settings.LaunchOnStartup);
            // 注册表可能被组策略或用户手动修改；以实际状态回写，避免设置页显示假状态。
            Settings.LaunchOnStartup = _startupLaunch.IsEnabled();
            PersistSignatures();
            await _settings.SaveAsync();
            // 签名开关会影响账户卡片的「签名已启用 / 未启用」标记
            RebuildAccountCards();
            _theme.Apply(Settings.Theme);
            OnPropertyChanged(nameof(IsDarkPreview));
            RefreshTemplatePreview();
            StatusText = "设置已保存";

            // 地址写错时仍保存用户输入（避免丢失），但明确告知不会发起自动检测
            if (UpdateAddressIsError)
            {
                StatusText = "设置已保存，但升级服务地址不合法，自动检测不会发起：" + UpdateAddressPreview;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存设置失败");
            StatusText = "保存失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, true);
    }

    // ---------------- 账户管理 ----------------

    public async Task InitializeAsync()
    {
        Settings.LaunchOnStartup = _startupLaunch.IsEnabled();
        await ReloadAccountsAsync();
        await ReloadRulesAsync();
        await ReloadSenderFiltersAsync();
        await ReloadTemplatesAsync();
        SyncAutoLockIndex();
        SyncGlobalHotkeyState();
        ImportAccount ??= Accounts.FirstOrDefault();
    }

    // ---------------- 签名（按账户） ----------------

    public bool HasSelectedSignatureAccount => SelectedSignatureAccount is not null;

    public string SignatureAccountCountText => $"{SignatureAccounts.Count} 个账户";

    public string SignatureAccountTitle => SelectedSignatureAccount is { } item
        ? $"当前账户：{item.Name} {item.Email}"
        : "未选择账户";

    public string SignatureHint
    {
        get
        {
            if (SelectedSignatureAccount is not { } item) return "先在左侧选择一个邮箱账户";
            if (!item.IsEnabled) return "已关闭：用该账户写信时不会追加签名。";

            return string.IsNullOrWhiteSpace(HtmlTextExtractor.ToPlainText(item.Html))
                ? "已启用，但签名内容还是空的，写一段吧。"
                : "已启用：用该账户写信时，发送前会自动在正文末尾追加这段签名。";
        }
    }

    /// <summary>正在把选中账户的签名灌进编辑器，避免回写把别的账户覆盖掉。</summary>
    private bool _loadingSignature;

    /// <summary>按账户重建签名条目：已单独设置过的用设置值，没有的用旧版全局签名兜底。</summary>
    private void RebuildSignatureAccounts()
    {
        var selectedId = SelectedSignatureAccount?.AccountId;

        SignatureAccounts.Clear();
        foreach (var account in Accounts)
        {
            var entry = Settings.FindSignature(account.Id);
            var legacyHtml = entry is null && !string.IsNullOrWhiteSpace(Settings.Signature)
                ? HtmlTextExtractor.ToHtml(Settings.Signature.Trim())
                : "";
            SignatureAccounts.Add(new SignatureAccountItem(account, entry, legacyHtml));
        }

        SelectedSignatureAccount = selectedId is { } id
            ? SignatureAccounts.FirstOrDefault(a => a.AccountId == id) ?? SignatureAccounts.FirstOrDefault()
            : SignatureAccounts.FirstOrDefault();

        OnPropertyChanged(nameof(SignatureAccountCountText));
    }

    partial void OnSelectedSignatureAccountChanged(SignatureAccountItem? oldValue, SignatureAccountItem? newValue)
    {
        _loadingSignature = true;
        SignatureHtml = newValue?.Html ?? "";
        SignatureEnabled = newValue?.IsEnabled ?? false;
        _loadingSignature = false;

        OnPropertyChanged(nameof(HasSelectedSignatureAccount));
        OnPropertyChanged(nameof(SignatureAccountTitle));
        OnPropertyChanged(nameof(SignatureHint));
    }

    partial void OnSignatureHtmlChanged(string value)
    {
        if (_loadingSignature) return;
        if (SelectedSignatureAccount is { } item) item.Html = value;
        OnPropertyChanged(nameof(SignatureHint));
    }

    partial void OnSignatureEnabledChanged(bool value)
    {
        if (_loadingSignature) return;
        if (SelectedSignatureAccount is { } item) item.IsEnabled = value;
        OnPropertyChanged(nameof(SignatureHint));
    }

    /// <summary>把签名页的编辑结果写回设置对象（保存设置时调用）。</summary>
    private void PersistSignatures()
    {
        Settings.Signatures = SignatureAccounts
            .Select(item => new AccountSignature
            {
                AccountId = item.AccountId,
                IsEnabled = item.IsEnabled,
                Html = item.Html ?? ""
            })
            .ToList();
    }

    // ---------------- 签名导入 / 导出（换机器、同事之间分享） ----------------

    /// <summary>
    /// 导出所有账户的签名：只带写了内容的账户，按邮箱地址标识（换机器导入时账户 id 不一样）。
    /// </summary>
    [RelayCommand]
    private async Task ExportSignaturesAsync()
    {
        try
        {
            var entries = SignatureAccounts
                .Where(item => !string.IsNullOrWhiteSpace(item.Email) && !string.IsNullOrWhiteSpace(item.Html))
                .Select(item => new SignatureShareEntry
                {
                    Account = item.Email,
                    Enabled = item.IsEnabled,
                    Html = item.Html ?? ""
                })
                .ToList();

            if (entries.Count == 0)
            {
                _dialogs.Alert("没有可导出的签名", "先给账户写一段签名，再导出分享。", DialogIcon.Info);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "导出签名",
                Filter = "ZungMail 签名文件 (*.json)|*.json",
                DefaultExt = ".json",
                AddExtension = true,
                FileName = $"ZungMail-签名-{DateTime.Now:yyyyMMdd}.json"
            };

            if (dialog.ShowDialog() != true) return;

            var json = SignatureShareService.Serialize(entries);
            await File.WriteAllTextAsync(dialog.FileName, json, new System.Text.UTF8Encoding(false))
                .ConfigureAwait(true);

            StatusText = $"已导出 {entries.Count} 个账户的签名到 {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出签名失败");
            _dialogs.Alert("导出失败", ex.Message, DialogIcon.Error);
        }
    }

    /// <summary>
    /// 导入签名文件：按邮箱地址匹配本地账户，命中的覆盖该账户现有签名，本地没有的邮箱只在确认框里提示。
    /// </summary>
    [RelayCommand]
    private async Task ImportSignaturesAsync()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入签名",
                Filter = "ZungMail 签名文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true) return;

            var json = await File.ReadAllTextAsync(dialog.FileName).ConfigureAwait(true);
            var parsed = SignatureShareService.Parse(json);
            if (!parsed.Ok)
            {
                _dialogs.Alert("无法导入", parsed.Message, DialogIcon.Warning);
                return;
            }

            var plan = SignatureShareService.Plan(
                parsed.Entries,
                SignatureAccounts.Select(item => (item.AccountId, item.Email)));

            if (!plan.HasAnything)
            {
                var unknownList = plan.UnknownAccounts.Count > 0
                    ? string.Join("、", plan.UnknownAccounts)
                    : "（文件里没有账户信息）";
                _dialogs.Alert(
                    "没有可导入的签名",
                    $"文件里的邮箱在本机都没有对应账户：{unknownList}\n\n先在设置 → 账户里添加对应邮箱，再导入。",
                    DialogIcon.Warning);
                return;
            }

            var detail = $"将更新 {plan.Matched.Count} 个账户的签名，覆盖它们当前的签名内容；其它账户不受影响。";
            if (plan.UnknownAccounts.Count > 0)
            {
                detail += $"\n\n本机没有这些邮箱，会被跳过：{string.Join("、", plan.UnknownAccounts)}";
            }

            if (!_dialogs.Confirm(
                    "导入签名",
                    $"从「{Path.GetFileName(dialog.FileName)}」导入？",
                    "导入",
                    detail,
                    DialogIcon.Question))
            {
                return;
            }

            foreach (var match in plan.Matched)
            {
                var item = SignatureAccounts.FirstOrDefault(a => a.AccountId == match.AccountId);
                if (item is null) continue;

                item.Html = match.Entry.Html ?? "";
                item.IsEnabled = match.Entry.Enabled;
            }

            // 导入即落盘：签名是账户级设置，用户不会为了生效再去点一次「保存设置」
            PersistSignatures();
            await _settings.SaveAsync();

            // 重建条目让左侧状态（已设置 / 未设置）与右侧编辑器同步刷新
            RebuildSignatureAccounts();
            StatusText = $"已导入 {plan.Matched.Count} 个账户的签名";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导入签名失败");
            _dialogs.Alert("导入失败", ex.Message, DialogIcon.Error);
        }
    }

    private async Task ReloadAccountsAsync()
    {
        try
        {
            var accounts = await _accounts.GetAllAsync();
            var selectedId = SelectedAccount?.Id;

            _accountStats.Clear();
            foreach (var account in accounts)
            {
                try
                {
                    var total = await _messages.CountAsync(account.Id);
                    var unread = await _messages.CountUnreadAsync(account.Id);
                    _accountStats[account.Id] = (total, unread);
                }
                catch (Exception ex)
                {
                    // 统计失败不影响账户列表本身
                    _logger.LogWarning(ex, "读取账户本地邮件统计失败：{Id}", account.Id);
                }
            }

            Accounts.Clear();
            foreach (var account in accounts) Accounts.Add(account);

            SelectedAccount = selectedId is { } id
                ? Accounts.FirstOrDefault(a => a.Id == id)
                : Accounts.FirstOrDefault();

            RebuildAccountCards();

            // 账户增删后签名页的条目要跟着变
            RebuildSignatureAccounts();

            // 从主窗口账户右键进来时预选的账户：列表加载完再落一次
            ApplyPreselectedAccount();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取账户列表失败");
            StatusText = "读取账户失败：" + ex.Message;
        }
    }

    /// <summary>按账户 + 已缓存统计重建卡片；签名开关变化时也走这里。</summary>
    private void RebuildAccountCards()
    {
        var selectedId = SelectedAccount?.Id;

        AccountCards.Clear();
        foreach (var account in Accounts)
        {
            var stats = _accountStats.TryGetValue(account.Id, out var value) ? value : (0, 0);
            AccountCards.Add(AccountCardItem.Create(account, stats.Item1, stats.Item2, Settings));
        }

        SelectedAccountCard = selectedId is { } id
            ? AccountCards.FirstOrDefault(card => card.Id == id)
            : AccountCards.FirstOrDefault();

        OnPropertyChanged(nameof(AccountListSummary));
        OnPropertyChanged(nameof(HasNoAccounts));
        OnPropertyChanged(nameof(HasSelectedAccount));
        OnPropertyChanged(nameof(CanRunAccountAction));
    }

    /// <summary>
    /// 预选某个账户（从主窗口账户右键「设置」进来时调用）。
    /// 账户列表是异步加载的，这里先记下 id，列表加载完再真正选中。
    /// </summary>
    public void SelectAccountById(long accountId)
    {
        _preselectedAccountId = accountId;
        ApplyPreselectedAccount();
    }

    private long? _preselectedAccountId;

    private void ApplyPreselectedAccount()
    {
        if (_preselectedAccountId is not { } accountId) return;

        var account = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (account is null) return;

        _preselectedAccountId = null;
        SelectedAccount = account;
        SelectedAccountCard = AccountCards.FirstOrDefault(card => card.Id == accountId) ?? SelectedAccountCard;
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        if (_dialogs.ShowAccountEditor(null))
        {
            await ReloadAccountsAsync();
            StatusText = "账户已添加，可在主窗口点击刷新开始同步";
        }
    }

    [RelayCommand]
    private async Task EditAccountAsync()
    {
        if (SelectedAccount is not { } account) return;

        if (_dialogs.ShowAccountEditor(account))
        {
            await ReloadAccountsAsync();
            StatusText = "账户已更新";
        }
    }

    [RelayCommand]
    private async Task DeleteAccountAsync()
    {
        if (SelectedAccount is not { } account) return;

        var choice = _dialogs.Ask(new MessageDialogOptions
        {
            Title = "删除账户",
            Message = $"确定删除账户「{account.Name} <{account.Email}>」吗？",
            Detail = "「删除账户与邮件」会同时清空本地缓存的邮件，不可撤销；"
                     + "只想暂时不用的话，选择「仅停止使用」即可，本地邮件与设置全部保留。",
            Icon = DialogIcon.Warning,
            PrimaryText = "删除账户与邮件",
            SecondaryText = "仅停止使用",
            CancelText = "取消",
            Danger = true
        });

        if (choice == DialogChoice.Cancel) return;

        try
        {
            var deleteMessages = choice == DialogChoice.Primary;
            await _accounts.DeleteAsync(account.Id, deleteMessages: deleteMessages);
            await ReloadAccountsAsync();
            StatusText = deleteMessages ? "账户已删除" : "账户已停止使用（本地邮件保留）";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除账户失败：{Id}", account.Id);
            StatusText = "删除失败：" + ex.Message;
        }
    }

    /// <summary>把选中账户设为主账户：同步与账户列表都优先它，其余自动降为副账户。</summary>
    [RelayCommand]
    private async Task SetPrimaryAccountAsync()
    {
        if (SelectedAccount is not { } account || account.IsPrimary) return;

        try
        {
            await _accounts.SetPrimaryAsync(account.Id);
            await ReloadAccountsAsync();
            StatusText = $"「{account.Name}」已设为主账户，同步时优先刷新";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置主账户失败：{Id}", account.Id);
            StatusText = "设置主账户失败：" + ex.Message;
        }
    }

    /// <summary>启用 / 停止使用账户：停止后不参与同步与自动发送，本地邮件保留。</summary>
    [RelayCommand]
    private async Task ToggleAccountEnabledAsync()
    {
        if (SelectedAccount is not { } account) return;

        var target = !account.IsEnabled;
        var confirm = target
            ? _dialogs.Confirm(
                "启用账户",
                $"恢复使用「{account.Email}」吗？",
                primaryText: "启用",
                detail: "启用后会重新参与收信与定时发送。",
                icon: DialogIcon.Question)
            : _dialogs.Confirm(
                "停止使用",
                $"停止使用「{account.Email}」吗？",
                primaryText: "停止使用",
                detail: "停止后不再收信、不再自动发送，本地邮件与设置全部保留，可随时重新启用。",
                icon: DialogIcon.Question);
        if (!confirm) return;

        await _accounts.SetEnabledAsync(account.Id, target);
        await ReloadAccountsAsync();
        StatusText = target ? "账户已启用" : "账户已停止使用";
    }
    [RelayCommand]
    private async Task SyncAccountAsync()
    {
        if (SelectedAccount is not { } account || IsBusy) return;

        IsBusy = true;
        StatusText = $"正在同步「{account.Name}」…";
        try
        {
            var outcome = await _sync.SyncAccountAsync(account.Id);
            StatusText = outcome.Summary;
            await ReloadAccountsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步账户失败：{Id}", account.Id);
            StatusText = "同步失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 修复历史乱码：清空该账户的本地邮件后按正确字符集重新拉取邮件头。
    /// 乱码是同步入库时就写坏的，只能重新解析服务器原件才能修正。
    /// </summary>
    [RelayCommand]
    private async Task RepairEncodingAsync()
    {
        if (SelectedAccount is not { } account || IsBusy) return;

        var confirm = _dialogs.Confirm(
            "修复乱码（全量重新同步）",
            $"将清空「{account.Name}」的本地邮件并重新拉取邮件头，用于修复 GBK / KOI8-R 等旧字符集乱码。",
            primaryText: "重新同步",
            detail: "副作用：该账户已下载的正文与附件、本地已读与星标状态会被清空，随后以服务器状态为准重新同步。",
            icon: DialogIcon.Warning,
            danger: true);

        if (!confirm) return;

        IsBusy = true;
        StatusText = $"正在重新同步「{account.Name}」（修复乱码）…";
        try
        {
            var outcome = await _sync.SyncAccountAsync(account.Id, fullResync: true);
            StatusText = outcome.Success ? "乱码修复同步完成 · " + outcome.Summary : outcome.Summary;
            await ReloadAccountsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "修复乱码同步失败：{Id}", account.Id);
            StatusText = "修复乱码失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------- 模板管理 ----------------

    /// <summary>可视化内容块：点击后插入到编辑区光标处。</summary>
    private static readonly IReadOnlyList<TemplateBlock> Blocks = new[]
    {
        new TemplateBlock("标题", "插入二级标题", "<h2>章节标题</h2>"),
        new TemplateBlock("段落", "插入正文段落", "<p>在此输入正文内容</p>"),
        new TemplateBlock("项目列表", "插入无序列表", "<ul><li>要点一</li><li>要点二</li></ul>"),
        new TemplateBlock("编号步骤", "插入有序列表", "<ol><li>第一步</li><li>第二步</li></ol>"),
        new TemplateBlock("表格", "插入 3 行 3 列表格",
            "<table border=\"1\" style=\"border-collapse:collapse;width:100%\">" +
            "<thead><tr><th>项目</th><th>负责人</th><th>备注</th></tr></thead>" +
            "<tbody><tr><td>&nbsp;</td><td>&nbsp;</td><td>&nbsp;</td></tr>" +
            "<tr><td>&nbsp;</td><td>&nbsp;</td><td>&nbsp;</td></tr></tbody></table>"),
        new TemplateBlock("引用", "插入引用块", "<blockquote>引用或结论性内容</blockquote>"),
        new TemplateBlock("分隔线", "插入分隔线", "<hr/>"),
        new TemplateBlock("落款", "插入结束语", "<p>此致<br/>敬礼</p><p>发件人</p>")
    };

    public IReadOnlyList<TemplateBlock> TemplateBlocks => Blocks;

    /// <summary>分组候选值（可直接在编辑区输入自定义分组）；含快速文本预置分组。</summary>
    public IReadOnlyList<string> TemplateCategoryOptions { get; } = new[]
        {
            MailTemplate.DefaultCategory, "工作汇报", "会议协作", "客户沟通", "个人事务"
        }
        .Concat(JsonTemplateStore.QuickTextCategories)
        .ToArray();

    /// <summary>可插入的占位符令牌。</summary>
    public IReadOnlyList<TemplatePlaceholderToken> TemplateTokens => TemplatePlaceholder.All;

    public ObservableCollection<TemplateListItem> Templates { get; } = new();

    /// <summary>当前 Tab 下的列表（邮件模板 / 快速文本），列表控件绑定它，<see cref="Templates"/> 始终是全集。</summary>
    public ObservableCollection<TemplateListItem> VisibleTemplates { get; } = new();

    /// <summary>当前模板中需要用户手工填写的占位符取值。</summary>
    public ObservableCollection<TemplatePlaceholderEntry> TemplatePlaceholderEntries { get; } = new();

    public string TemplateCountText => IsQuickTextTabActive
        ? $"{VisibleTemplates.Count} 条快速文本"
        : $"{VisibleTemplates.Count} 个模板";

    // ---------------- 模板 / 快速文本 两个 Tab ----------------

    /// <summary>右侧是否在编辑快速文本（纯文本话术）；false 表示整封邮件模板（富文本 + 主题 + 占位符）。</summary>
    [ObservableProperty] private bool _isQuickTextTabActive;

    /// <summary>「邮件模板」Tab 是否选中；写入 true 时切回模板。</summary>
    public bool IsMailTemplateTabActive
    {
        get => !IsQuickTextTabActive;
        set
        {
            if (value && IsQuickTextTabActive) IsQuickTextTabActive = false;
        }
    }

    public string TemplateListTitle => IsQuickTextTabActive ? "快速文本" : "模板列表";

    public string TemplateContentTitle => IsQuickTextTabActive ? "快速文本内容" : "模板内容（FR-08）";

    public string TemplateNewButtonText => IsQuickTextTabActive ? "新建快速文本" : "新建模板";

    public string TemplateSaveButtonText => IsQuickTextTabActive ? "保存快速文本" : "保存模板";

    public string TemplateNameLabel => IsQuickTextTabActive ? "快速文本名称" : "模板名称";

    public string TemplateDeleteButtonText => IsQuickTextTabActive ? "删除快速文本" : "删除模板";

    public string TemplateEmptyHint => IsQuickTextTabActive
        ? "从左侧选择一条快速文本，或点击「新建快速文本」开始编辑"
        : "从左侧选择一个模板，或点击「新建模板」开始编辑";

    [RelayCommand]
    private void ShowMailTemplateTab() => IsQuickTextTabActive = false;

    [RelayCommand]
    private void ShowQuickTextTab() => IsQuickTextTabActive = true;

    partial void OnIsQuickTextTabActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMailTemplateTabActive));
        OnPropertyChanged(nameof(TemplateListTitle));
        OnPropertyChanged(nameof(TemplateContentTitle));
        OnPropertyChanged(nameof(TemplateNewButtonText));
        OnPropertyChanged(nameof(TemplateSaveButtonText));
        OnPropertyChanged(nameof(TemplateNameLabel));
        OnPropertyChanged(nameof(TemplateDeleteButtonText));
        OnPropertyChanged(nameof(TemplateEmptyHint));
        OnPropertyChanged(nameof(TemplateCountText));
        OnPropertyChanged(nameof(IsPlaceholderValuePanelVisible));

        ApplyTemplateKindFilter();

        // 快速文本没有富文本预览；切回模板 Tab 时补一次
        if (!value) RefreshTemplatePreview();
    }

    /// <summary>按当前 Tab 重建可见列表，并尽量保持选中项。</summary>
    private void ApplyTemplateKindFilter(string? preferId = null)
    {
        var keepId = preferId ?? SelectedTemplate?.Model.Id;

        VisibleTemplates.Clear();
        foreach (var item in Templates.Where(t => t.IsQuickText == IsQuickTextTabActive))
        {
            VisibleTemplates.Add(item);
        }

        SelectedTemplate = VisibleTemplates.FirstOrDefault(t => t.Model.Id == keepId)
                           ?? VisibleTemplates.FirstOrDefault();

        OnPropertyChanged(nameof(TemplateCountText));
    }

    public bool HasSelectedTemplate => SelectedTemplate is not null;

    public bool IsBuiltinTemplate => SelectedTemplate?.IsBuiltIn == true;

    public bool CanDeleteTemplate => SelectedTemplate is { IsBuiltIn: false };

    public bool IsTemplateDirty => SelectedTemplate?.IsDirty == true;

    public bool HasUnsavedTemplateChanges => Templates.Any(t => t.IsDirty);

    public bool HasTemplateErrors =>
        TemplateNameError.Length > 0 || TemplateSubjectError.Length > 0 || TemplateBodyError.Length > 0;

    public bool IsTemplateEditorVisible => TemplateViewMode != TemplateViewMode.Preview;

    public bool IsTemplatePreviewVisible => TemplateViewMode != TemplateViewMode.Edit;

    public bool IsTemplateSplitMode => TemplateViewMode == TemplateViewMode.Split;

    /// <summary>
    /// 编辑区行高：全预览时收起，编辑/并排时占剩余空间。
    /// 编辑区是 WebView2（独立 HWND），必须由外层行高精确限制，否则会盖住窗口底部区域。
    /// 并排时编辑区占 2/3，留够工具栏与正文，预览占 1/3。
    /// </summary>
    public System.Windows.GridLength TemplateEditorRowHeight => TemplateViewMode switch
    {
        TemplateViewMode.Preview => new System.Windows.GridLength(0),
        TemplateViewMode.Split => new System.Windows.GridLength(2, System.Windows.GridUnitType.Star),
        _ => new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
    };

    /// <summary>预览区行高：仅编辑时收起，并排时占 1/3，全预览时铺满。</summary>
    public System.Windows.GridLength TemplatePreviewRowHeight => TemplateViewMode switch
    {
        TemplateViewMode.Edit => new System.Windows.GridLength(0),
        _ => new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
    };

    /// <summary>占位符取值面板只对整封模板有意义（快速文本是纯文本话术）。</summary>
    public bool IsPlaceholderValuePanelVisible => !IsQuickTextTabActive && TemplatePlaceholderEntries.Count > 0;

    /// <summary>预览渲染使用的主题（跟随已保存的主题设置）。</summary>
    public bool IsDarkPreview => _theme.IsDark;

    /// <summary>在设置窗口内点击「应用模板」后，需要打开的写信请求。</summary>
    public ComposeRequest? PendingComposeRequest { get; private set; }

    private bool _loadingTemplate;
    private CancellationTokenSource? _previewCts;

    private async Task ReloadTemplatesAsync()
    {
        try
        {
            var templates = await _templates.GetAllAsync();
            var selectedId = SelectedTemplate?.Model.Id;

            _loadingTemplate = true;
            Templates.Clear();
            foreach (var template in templates) Templates.Add(new TemplateListItem(template));
            _loadingTemplate = false;

            ApplyTemplateKindFilter(selectedId);

            NotifyTemplateListChanged();

            // 模板文件损坏时会回退内置模板；把备份文件名提示到状态栏，便于用户找回
            StatusText = _templates.LoadWarning
                ?? (VisibleTemplates.Count == 0 ? "这里还没有内容，点击「新建」创建第一条" : "就绪");
        }
        catch (Exception ex)
        {
            _loadingTemplate = false;
            _logger.LogError(ex, "读取模板失败");
            StatusText = "读取模板失败：" + ex.Message;
        }
    }

    partial void OnSelectedTemplateChanged(TemplateListItem? oldValue, TemplateListItem? newValue)
    {
        LoadTemplate(newValue);
        OnPropertyChanged(nameof(HasSelectedTemplate));
        OnPropertyChanged(nameof(IsBuiltinTemplate));
        OnPropertyChanged(nameof(CanDeleteTemplate));
        OnPropertyChanged(nameof(IsTemplateDirty));
    }

    private void LoadTemplate(TemplateListItem? item)
    {
        _loadingTemplate = true;

        TemplateName = item?.Model.Name ?? "";
        TemplateSubject = item?.Model.Subject ?? "";
        TemplateBody = item?.Model.Body ?? "";
        TemplateDescription = item?.Model.Description ?? "";
        TemplateCategory = string.IsNullOrWhiteSpace(item?.Model.Category)
            ? MailTemplate.DefaultCategory
            : item!.Model.Category;

        ClearTemplateErrors();
        RebuildPlaceholderEntries();
        RefreshTemplatePreview();

        _loadingTemplate = false;
    }

    partial void OnTemplateNameChanged(string value)
    {
        if (_loadingTemplate) return;
        if (SelectedTemplate is { } item) item.Model.Name = value;
        TemplateNameError = "";
        UpdateTemplateDirty();
    }

    partial void OnTemplateSubjectChanged(string value)
    {
        if (_loadingTemplate) return;
        if (SelectedTemplate is { } item) item.Model.Subject = value;
        TemplateSubjectError = "";
        UpdateTemplateDirty();
        RebuildPlaceholderEntries();
        ScheduleTemplatePreview();
    }

    partial void OnTemplateBodyChanged(string value)
    {
        if (_loadingTemplate) return;
        if (SelectedTemplate is { } item) item.Model.Body = value;
        TemplateBodyError = "";
        UpdateTemplateDirty();
        RebuildPlaceholderEntries();
        ScheduleTemplatePreview();
    }

    partial void OnTemplateDescriptionChanged(string value)
    {
        if (_loadingTemplate) return;
        if (SelectedTemplate is { } item) item.Model.Description = value;
        UpdateTemplateDirty();
    }

    partial void OnTemplateCategoryChanged(string value)
    {
        if (_loadingTemplate) return;
        if (SelectedTemplate is { } item) item.Model.Category = value;
        UpdateTemplateDirty();
    }

    partial void OnTemplateViewModeChanged(TemplateViewMode value)
    {
        OnPropertyChanged(nameof(IsTemplateEditorVisible));
        OnPropertyChanged(nameof(IsTemplatePreviewVisible));
        OnPropertyChanged(nameof(IsTemplateSplitMode));
        OnPropertyChanged(nameof(TemplateEditorRowHeight));
        OnPropertyChanged(nameof(TemplatePreviewRowHeight));

        if (value != TemplateViewMode.Edit) RefreshTemplatePreview();
    }

    private void UpdateTemplateDirty()
    {
        SelectedTemplate?.Refresh();
        OnPropertyChanged(nameof(IsTemplateDirty));
        OnPropertyChanged(nameof(HasUnsavedTemplateChanges));
        OnPropertyChanged(nameof(CanDeleteTemplate));
    }

    private void ClearTemplateErrors()
    {
        TemplateNameError = "";
        TemplateSubjectError = "";
        TemplateBodyError = "";
        TemplateHint = "";
        OnPropertyChanged(nameof(HasTemplateErrors));
    }

    // ---- 实时预览 ----

    private void ScheduleTemplatePreview()
    {
        if (_disposed) return;

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // 输入过程中合并刷新，避免每次按键都重新导航预览页
                await Task.Delay(220, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested || _disposed) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            await dispatcher.InvokeAsync(() =>
            {
                // 排队期间窗口可能已关闭：已释放就别再碰控件与 WebView2
                if (!_disposed) RefreshTemplatePreview();
            });
        }, token);
    }

    private void RefreshTemplatePreview()
    {
        // 纯编辑态不渲染预览文档：预览控件不可见时无需驱动 WebView2 导航
        if (TemplateViewMode == TemplateViewMode.Edit) return;

        var values = BuildPlaceholderValues();

        var subject = TemplatePlaceholder.Resolve(TemplateSubject, values);
        TemplatePreviewSubject = string.IsNullOrWhiteSpace(subject) ? "（未设置默认主题）" : subject;

        var body = TemplatePlaceholder.Resolve(TemplateBody, values);
        if (string.IsNullOrWhiteSpace(body)) body = "<p style=\"color:#8B93A7\">（正文为空）</p>";

        var highlighted = TemplatePlaceholder.HighlightUnresolved(body);
        TemplatePreviewSource = highlighted;
        TemplatePreviewDocument = HtmlMailDocumentBuilder.Build(highlighted, new MailDocumentOptions
        {
            IsDark = _theme.IsDark,
            FontSize = 13.5,
            AllowRemoteImages = true
        });
    }

    private IReadOnlyDictionary<string, string?> BuildPlaceholderValues()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in TemplatePlaceholderEntries) values[entry.Token] = entry.Value;
        return TemplatePlaceholder.MergeValues(DateTimeOffset.Now, values);
    }

    private void RebuildPlaceholderEntries()
    {
        var text = (TemplateSubject ?? "") + "\n" + (TemplateBody ?? "");
        var autoTokens = TemplatePlaceholder.AutoValues(DateTimeOffset.Now).Keys;
        var needed = TemplatePlaceholder.Unresolved(text)
            .Where(t => !autoTokens.Contains(t.Token))
            .ToList();

        for (var i = TemplatePlaceholderEntries.Count - 1; i >= 0; i--)
        {
            if (needed.All(t => t.Token != TemplatePlaceholderEntries[i].Token))
            {
                TemplatePlaceholderEntries[i].PropertyChanged -= OnPlaceholderValueChanged;
                TemplatePlaceholderEntries.RemoveAt(i);
            }
        }

        foreach (var token in needed)
        {
            if (TemplatePlaceholderEntries.Any(e => e.Token == token.Token)) continue;
            var entry = new TemplatePlaceholderEntry(token.Token, token.DisplayName);
            entry.PropertyChanged += OnPlaceholderValueChanged;
            TemplatePlaceholderEntries.Add(entry);
        }

        OnPropertyChanged(nameof(IsPlaceholderValuePanelVisible));
    }

    private void OnPlaceholderValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TemplatePlaceholderEntry.Value)) return;
        ScheduleTemplatePreview();
    }

    // ---- 新建 / 复制 / 保存 / 删除 / 应用 ----

    [RelayCommand]
    private void NewTemplate()
    {
        // 当前 Tab 决定新建的类型：快速文本 Tab 里建的就是快速文本
        var quick = IsQuickTextTabActive;
        var model = new MailTemplate
        {
            Name = UniqueTemplateName(quick ? "新快速文本" : "新模板"),
            Category = quick
                ? JsonTemplateStore.QuickTextCategories[0]
                : (string.IsNullOrWhiteSpace(TemplateCategory) ? MailTemplate.DefaultCategory : TemplateCategory),
            Body = "",
            IsQuickText = quick,
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        var item = new TemplateListItem(model);
        Templates.Insert(0, item);
        ApplyTemplateKindFilter(model.Id);

        NotifyTemplateListChanged();
        OnPropertyChanged(nameof(IsTemplateDirty));
        StatusText = quick ? "已新建快速文本，填写话术后点击「保存快速文本」" : "已新建模板，编辑内容后点击「保存模板」";
    }

    [RelayCommand]
    private async Task DuplicateTemplateAsync()
    {
        if (SelectedTemplate is not { } source) return;

        var copy = source.Model.Clone(newId: true);
        copy.Name = UniqueTemplateName($"{source.Model.Name} 副本");
        copy.IsBuiltIn = false;
        copy.UpdatedUtc = DateTimeOffset.UtcNow;

        var index = Math.Max(0, Templates.IndexOf(source));
        var item = new TemplateListItem(copy);
        Templates.Insert(index + 1, item);
        ApplyTemplateKindFilter(copy.Id);

        NotifyTemplateListChanged();
        await PersistTemplatesAsync($"已创建副本「{copy.Name}」");
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        if (SelectedTemplate is not { } item)
        {
            StatusText = IsQuickTextTabActive ? "请先新建或选择一条快速文本" : "请先新建或选择一个模板";
            return;
        }

        if (!ValidateSelectedTemplate(out var result))
        {
            StatusText = "未保存：" + result.ErrorSummary;
            return;
        }

        item.Model.UpdatedUtc = DateTimeOffset.UtcNow;
        var savedLabel = item.IsQuickText ? "快速文本已保存" : "模板已保存";
        await PersistTemplatesAsync(result.Warnings.Count > 0
            ? savedLabel + "（" + result.WarningSummary + "）"
            : savedLabel);
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync()
    {
        if (SelectedTemplate is not { } item) return;

        if (item.IsBuiltIn)
        {
            _dialogs.Alert(
                "内置模板",
                $"「{item.Model.Name}」是内置模板，不能删除。",
                DialogIcon.Info,
                "可以直接修改内容后保存，或点击「恢复默认」还原内置内容。");
            return;
        }

        var confirm = _dialogs.Confirm(
            item.IsQuickText ? "删除快速文本" : "删除模板",
            item.IsQuickText
                ? $"确定删除快速文本「{item.Model.Name}」吗？"
                : $"确定删除模板「{item.Model.Name}」吗？",
            primaryText: "删除",
            detail: "此操作不可撤销。",
            icon: DialogIcon.Warning,
            danger: true);
        if (!confirm) return;

        Templates.Remove(item);
        ApplyTemplateKindFilter();

        NotifyTemplateListChanged();
        await PersistTemplatesAsync(item.IsQuickText ? "快速文本已删除" : "模板已删除");
    }

    // ---------------- 模板导入 / 导出（同事之间分享） ----------------

    /// <summary>导出为分享文件：默认导出当前 Tab，也可以一次带上两类。</summary>
    [RelayCommand]
    private async Task ExportTemplatesAsync()
    {
        try
        {
            var mailCount = Templates.Count(t => !t.IsQuickText);
            var quickCount = Templates.Count(t => t.IsQuickText);
            if (mailCount + quickCount == 0)
            {
                _dialogs.Alert("没有可导出的模板", "先创建模板或快速文本，再导出分享。", DialogIcon.Info);
                return;
            }

            var onQuickTab = IsQuickTextTabActive;
            var currentCount = onQuickTab ? quickCount : mailCount;
            var includeAll = true;

            if (currentCount > 0)
            {
                var scope = _dialogs.Ask(new MessageDialogOptions
                {
                    Title = "导出模板",
                    Message = "要导出哪些内容？",
                    Detail = $"当前 Tab：{(onQuickTab ? "快速文本" : "邮件模板")} {currentCount} 条；"
                             + $"全部：邮件模板 {mailCount} 条 + 快速文本 {quickCount} 条。",
                    Icon = DialogIcon.Question,
                    PrimaryText = onQuickTab ? "只导出快速文本" : "只导出邮件模板",
                    SecondaryText = "导出全部",
                    CancelText = "取消"
                });

                if (scope == DialogChoice.Cancel) return;
                includeAll = scope == DialogChoice.Secondary;
            }

            var selected = includeAll
                ? Templates.ToList()
                : Templates.Where(t => t.IsQuickText == onQuickTab).ToList();
            if (selected.Count == 0)
            {
                _dialogs.Alert("没有可导出的内容", "当前 Tab 是空的。", DialogIcon.Info);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "导出模板",
                Filter = "ZungMail 模板文件 (*.json)|*.json",
                DefaultExt = ".json",
                AddExtension = true,
                FileName = $"ZungMail-{(onQuickTab && !includeAll ? "快速文本" : "模板")}-{DateTime.Now:yyyyMMdd}.json"
            };

            if (dialog.ShowDialog() != true) return;

            var json = TemplateShareService.Serialize(selected.Select(t => t.Model));
            await File.WriteAllTextAsync(dialog.FileName, json, new System.Text.UTF8Encoding(false))
                .ConfigureAwait(true);

            StatusText = $"已导出 {selected.Count} 条到 {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出模板失败");
            _dialogs.Alert("导出失败", ex.Message, DialogIcon.Error);
        }
    }

    /// <summary>导入分享文件：校验格式 → 确认条数 → 同名自动加序号，不覆盖现有内容。</summary>
    [RelayCommand]
    private async Task ImportTemplatesAsync()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入模板",
                Filter = "ZungMail 模板文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true) return;

            var json = await File.ReadAllTextAsync(dialog.FileName).ConfigureAwait(true);
            var parsed = TemplateShareService.Parse(json);
            if (!parsed.Ok)
            {
                _dialogs.Alert("无法导入", parsed.Message, DialogIcon.Warning);
                return;
            }

            var imported = TemplateShareService.Materialize(
                parsed.Entries,
                Templates.Select(t => t.Model),
                DateTimeOffset.UtcNow);

            var confirm = _dialogs.Confirm(
                "导入模板",
                $"从「{Path.GetFileName(dialog.FileName)}」导入 {imported.Count} 条？",
                "导入",
                $"邮件模板 {parsed.TemplateCount} 条、快速文本 {parsed.QuickTextCount} 条；"
                + "重名的会自动加序号，不会覆盖你现有的模板。",
                DialogIcon.Question);

            if (!confirm) return;

            foreach (var template in imported) Templates.Add(new TemplateListItem(template));
            await PersistTemplatesAsync($"已导入 {imported.Count} 条模板").ConfigureAwait(true);
            NotifyTemplateListChanged();

            // 切到导入内容所在的 Tab，并选中第一条，方便立刻核对
            var first = imported[0];
            if (first.IsQuickText != IsQuickTextTabActive) IsQuickTextTabActive = first.IsQuickText;
            ApplyTemplateKindFilter(first.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导入模板失败");
            _dialogs.Alert("导入失败", ex.Message, DialogIcon.Error);
        }
    }

    [RelayCommand]
    private async Task RestoreBuiltinTemplateAsync()
    {
        if (SelectedTemplate is not { IsBuiltIn: true } item) return;

        var builtin = JsonTemplateStore.CreateBuiltinTemplates().FirstOrDefault(t =>
            string.Equals(t.Id, item.Model.Id, StringComparison.Ordinal) ||
            string.Equals(t.Name, item.Model.Name, StringComparison.OrdinalIgnoreCase));
        if (builtin is null) return;

        var confirm = _dialogs.Confirm(
            "恢复默认模板",
            $"将「{item.Model.Name}」恢复为内置默认内容吗？",
            primaryText: "恢复默认",
            detail: "当前修改会丢失。",
            icon: DialogIcon.Question);
        if (!confirm) return;

        item.Model.Subject = builtin.Subject;
        item.Model.Body = builtin.Body;
        item.Model.Description = builtin.Description;
        item.Model.Category = builtin.Category;
        item.Model.UpdatedUtc = DateTimeOffset.UtcNow;

        LoadTemplate(item);
        OnPropertyChanged(nameof(IsTemplateDirty));
        await PersistTemplatesAsync("已恢复内置模板默认内容");
    }

    /// <summary>把模板应用到一封新邮件：保存 → 填充占位符 → 关闭设置并打开写信窗口。</summary>
    [RelayCommand]
    private async Task ApplyTemplateAsync()
    {
        if (SelectedTemplate is not { } item)
        {
            StatusText = "请先选择一个模板";
            return;
        }

        if (item.IsQuickText)
        {
            StatusText = "快速文本不整封套用，请在写信窗口双击插入到正文";
            return;
        }

        if (!ValidateSelectedTemplate(out var result))
        {
            StatusText = "模板存在错误，无法套用：" + result.ErrorSummary;
            return;
        }

        if (item.IsDirty) await PersistTemplatesAsync("模板已保存");

        var values = BuildPlaceholderValues();
        var subject = TemplatePlaceholder.Resolve(item.Model.Subject, values);
        var body = TemplatePlaceholder.Resolve(item.Model.Body, values);

        var pending = TemplatePlaceholder.Unresolved(subject + "\n" + body);
        if (pending.Count > 0)
        {
            var confirm = _dialogs.Confirm(
                "套用模板",
                "以下占位符尚未填写，将原样保留在邮件中。",
                primaryText: "继续套用",
                detail: string.Join("、", pending.Select(p => p.Token)),
                icon: DialogIcon.Question);
            if (!confirm) return;
        }

        PendingComposeRequest = new ComposeRequest { Subject = subject, Body = body };
        CloseRequested?.Invoke(this, true);
    }

    private bool ValidateSelectedTemplate(out TemplateValidationResult result)
    {
        result = TemplateValidationResult.Empty;
        if (SelectedTemplate is not { } item) return false;

        var siblings = Templates
            .Where(t => !ReferenceEquals(t, item))
            .Select(t => t.Model)
            .ToList();

        result = TemplateValidator.Validate(item.Model, siblings);

        // 快速文本没有主题，「默认主题为空」这条提示对它没有意义
        if (item.IsQuickText && result.Warnings.Count > 0)
        {
            result = result with
            {
                Warnings = result.Warnings
                    .Where(w => !string.Equals(w.Field, nameof(MailTemplate.Subject), StringComparison.Ordinal))
                    .ToList()
            };
        }

        TemplateNameError = result.ErrorFor(nameof(MailTemplate.Name)) ?? "";
        TemplateSubjectError = result.ErrorFor(nameof(MailTemplate.Subject)) ?? "";
        TemplateBodyError = result.ErrorFor(nameof(MailTemplate.Body)) ?? "";
        TemplateHint = result.IsValid ? result.WarningSummary : result.ErrorSummary;

        OnPropertyChanged(nameof(HasTemplateErrors));
        return result.IsValid;
    }

    private async Task PersistTemplatesAsync(string message)
    {
        try
        {
            await _templates.SaveAsync(Templates.Select(t => t.Model).ToList());

            foreach (var item in Templates)
            {
                item.Snapshot = item.Model.Clone();
                item.Refresh();
            }

            StatusText = message;
            OnPropertyChanged(nameof(IsTemplateDirty));
            OnPropertyChanged(nameof(HasUnsavedTemplateChanges));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存模板失败");
            StatusText = "保存模板失败：" + ex.Message;
        }
    }

    private string UniqueTemplateName(string baseName)
    {
        var name = baseName.Trim();
        if (Templates.All(t => !string.Equals(t.Model.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            return name;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{name} ({i})";
            if (Templates.All(t => !string.Equals(t.Model.Name?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return $"{name} {Guid.NewGuid().ToString("N")[..4]}";
    }

    private void NotifyTemplateListChanged()
    {
        OnPropertyChanged(nameof(TemplateCountText));
        OnPropertyChanged(nameof(HasUnsavedTemplateChanges));
        OnPropertyChanged(nameof(HasSelectedTemplate));
        OnPropertyChanged(nameof(CanDeleteTemplate));
        OnPropertyChanged(nameof(IsBuiltinTemplate));
        OnPropertyChanged(nameof(IsTemplateDirty));
    }

    /// <summary>关闭窗口前的兜底：模板有未保存修改时提示保存。</summary>
    public bool ConfirmClose()
    {
        if (!HasUnsavedTemplateChanges) return true;

        var choice = _dialogs.Ask(new MessageDialogOptions
        {
            Title = "未保存的模板",
            Message = "模板有未保存的修改，是否保存后关闭？",
            Icon = DialogIcon.Question,
            PrimaryText = "保存并关闭",
            SecondaryText = "不保存",
            CancelText = "取消"
        });

        if (choice == DialogChoice.Cancel) return false;
        if (choice == DialogChoice.Primary) _ = SaveTemplatesSilentlyAsync();
        return true;
    }

    private async Task SaveTemplatesSilentlyAsync()
    {
        try
        {
            await _templates.SaveAsync(Templates.Select(t => t.Model).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭设置时保存模板失败");
        }
    }

    // ---------------- 安全（客户端锁定） ----------------

    public IReadOnlyList<string> AutoLockOptions { get; } = new[]
    {
        "不自动锁定", "空闲 1 分钟", "空闲 5 分钟", "空闲 15 分钟", "空闲 30 分钟"
    };

    /// <summary>下拉索引 → 自动锁定分钟数。</summary>
    private static readonly int[] AutoLockMinutesMap = { 0, 1, 5, 15, 30 };

    [ObservableProperty] private int _autoLockIndex;

    /// <summary>初始化/刷新下拉时不要回写设置。</summary>
    private bool _suspendAutoLockSave;

    public string ClientLockStatusText => _clientLock.IsConfigured
        ? (_clientLock.AutoLockMinutes > 0
            ? $"已启用 · 空闲 {_clientLock.AutoLockMinutes} 分钟自动锁定"
            : "已启用 · 仅手动锁定")
        : "未启用（设置密码后自动启用）";

    partial void OnAutoLockIndexChanged(int value)
    {
        if (_suspendAutoLockSave) return;

        var minutes = AutoLockMinutesMap[Math.Clamp(value, 0, AutoLockMinutesMap.Length - 1)];
        _ = SaveAutoLockAsync(minutes);
    }

    private async Task SaveAutoLockAsync(int minutes)
    {
        var result = await _clientLock.SetAutoLockMinutesAsync(minutes);
        StatusText = result.Message;
        OnPropertyChanged(nameof(ClientLockStatusText));
    }

    [RelayCommand]
    private async Task EnableClientLockAsync(string? password)
    {
        var result = await _clientLock.SetPasswordAsync(password);
        StatusText = result.Message;
        if (!result.Ok) return;

        SyncAutoLockIndex();
        OnPropertyChanged(nameof(ClientLockStatusText));
    }

    [RelayCommand]
    private async Task DisableClientLockAsync()
    {
        var result = await _clientLock.DisableAsync();
        StatusText = result.Message;
        SyncAutoLockIndex();
        OnPropertyChanged(nameof(ClientLockStatusText));
    }

    /// <summary>立即锁定并关闭设置窗口，让锁定遮罩回到主界面。</summary>
    [RelayCommand]
    private void LockClientNow()
    {
        if (!_clientLock.IsConfigured)
        {
            StatusText = "请先设置锁定密码";
            return;
        }

        _clientLock.Lock();
        StatusText = "已锁定";
        CloseRequested?.Invoke(this, true);
    }

    private void SyncAutoLockIndex()
    {
        _suspendAutoLockSave = true;
        try
        {
            var minutes = Math.Clamp(_clientLock.AutoLockMinutes, 0, 240);
            var index = Array.IndexOf(AutoLockMinutesMap, minutes);
            AutoLockIndex = index < 0 ? 0 : index;
        }
        finally
        {
            _suspendAutoLockSave = false;
        }
    }

    // ---------------- 全局热键 ----------------

    [ObservableProperty] private bool _globalHotkeyEnabled;
    [ObservableProperty] private string _globalHotkeyText = "Ctrl+Alt+M";
    [ObservableProperty] private string _globalHotkeyStatus = "";

    [RelayCommand]
    private async Task SaveGlobalHotkeyAsync()
    {
        var enabled = GlobalHotkeyEnabled;

        // 先规范化再注册，避免保存进设置的是 "control + alt + m" 这类等价但难读的写法。
        if (enabled && HotkeyGesture.TryParse(GlobalHotkeyText, out var gesture) && gesture is not null)
        {
            GlobalHotkeyText = gesture.Display;
        }

        var result = _hotkey.Apply(enabled, GlobalHotkeyText);
        GlobalHotkeyStatus = result.Message;
        StatusText = result.Message;
        if (!result.Ok) return;

        Settings.GlobalHotkeyEnabled = enabled;
        Settings.GlobalHotkey = enabled ? GlobalHotkeyText.Trim() : Settings.GlobalHotkey;
        await _settings.SaveAsync();
    }

    private void SyncGlobalHotkeyState()
    {
        GlobalHotkeyEnabled = Settings.GlobalHotkeyEnabled;
        GlobalHotkeyText = string.IsNullOrWhiteSpace(Settings.GlobalHotkey)
            ? "Ctrl+Alt+M"
            : Settings.GlobalHotkey;
        GlobalHotkeyStatus = _hotkey.IsRegistered
            ? $"已注册：{_hotkey.CurrentDisplay}"
            : "未注册（启用后立即生效，重启后自动恢复）";
    }

    // ---------------- 邮件导入（.eml） ----------------

    public ObservableCollection<MailFolder> ImportFolders { get; } = new();

    [ObservableProperty] private Account? _importAccount;
    [ObservableProperty] private MailFolder? _importFolder;
    [ObservableProperty] private string _importStatus = "选择账户与目标文件夹后，可批量导入 .eml 文件";
    [ObservableProperty] private bool _isImporting;

    partial void OnImportAccountChanged(Account? value) => _ = ReloadImportFoldersAsync(value);

    private async Task ReloadImportFoldersAsync(Account? account)
    {
        ImportFolders.Clear();
        if (account is null)
        {
            ImportFolder = null;
            return;
        }

        try
        {
            foreach (var folder in await _folders.GetByAccountAsync(account.Id))
            {
                ImportFolders.Add(folder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载导入目标文件夹失败");
            ImportStatus = "加载文件夹失败：" + ex.Message;
        }

        ImportFolder = ImportFolders.FirstOrDefault(folder => folder.Kind == FolderKind.Inbox)
                       ?? ImportFolders.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ImportEmlAsync()
    {
        if (ImportAccount is not { } account || ImportFolder is not { } folder)
        {
            ImportStatus = "请先选择账户与目标文件夹";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的邮件文件",
            Filter = "邮件文件 (*.eml)|*.eml|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        IsImporting = true;
        ImportStatus = $"正在导入 0/{dialog.FileNames.Length}…";
        try
        {
            var total = dialog.FileNames.Length;
            var progress = new Progress<int>(done => ImportStatus = $"正在导入 {done}/{total}…");
            var result = await _importer.ImportAsync(account.Id, folder.Id, dialog.FileNames, progress);

            ImportStatus = result.Failures.Count == 0
                ? result.Summary + "（切回主界面后点击刷新即可看到）"
                : $"{result.Summary}；首个失败：{result.Failures[0]}";
            StatusText = result.Summary;
            _logger.LogInformation("导入 .eml 完成：{Summary}", result.Summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导入 .eml 失败");
            ImportStatus = "导入失败：" + ex.Message;
        }
        finally
        {
            IsImporting = false;
        }
    }

    // ---------------- 联系人导入 / 导出 ----------------

    [ObservableProperty] private string _contactsStatus =
        "支持 vCard（.vcf）与 CSV（.csv）；CSV 表头可用「姓名,邮箱」或「Name,E-mail」";

    [RelayCommand]
    private async Task ImportContactsAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入联系人",
            Filter = "联系人文件 (*.vcf;*.csv)|*.vcf;*.csv|vCard (*.vcf)|*.vcf|CSV (*.csv)|*.csv"
        };
        if (dialog.ShowDialog() != true) return;

        ContactsStatus = "正在导入联系人…";
        var result = await _contactExchange.ImportAsync(dialog.FileName);
        ContactsStatus = result.Message;
        StatusText = result.Message;
    }

    [RelayCommand]
    private async Task ExportContactsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出联系人",
            Filter = "vCard (*.vcf)|*.vcf|CSV (*.csv)|*.csv",
            FileName = "contacts.vcf",
            DefaultExt = ".vcf",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true) return;

        var result = await _contactExchange.ExportAsync(dialog.FileName);
        ContactsStatus = result.Message;
        StatusText = result.Message;
    }

    // ---------------- 邮件规则 ----------------

    public ObservableCollection<MailRule> Rules { get; } = new();

    public IReadOnlyList<string> RuleActionOptions { get; } = new[]
    {
        "移动到文件夹", "加星标", "标记为已读", "归档", "标记为垃圾邮件", "打标签"
    };

    public ObservableCollection<MailFolder> RuleFolders { get; } = new();

    /// <summary>打标签动作可选的标签（与「标签管理」共用同一批数据）。</summary>
    public ObservableCollection<MailTag> RuleTags { get; } = new();

    [ObservableProperty] private MailRule? _selectedRule;
    [ObservableProperty] private string _ruleName = "";
    [ObservableProperty] private string _ruleFromContains = "";
    [ObservableProperty] private string _ruleFromDomain = "";
    [ObservableProperty] private string _ruleSubjectContains = "";
    [ObservableProperty] private int _ruleActionIndex;
    [ObservableProperty] private MailFolder? _ruleTargetFolder;
    [ObservableProperty] private MailTag? _ruleTag;
    [ObservableProperty] private bool _ruleEnabled = true;
    [ObservableProperty] private bool _ruleContinueAfterMatch;

    public bool IsRuleMoveAction => RuleActionIndex == 0;

    public bool IsRuleTagAction => RuleActionIndex == 5;

    partial void OnRuleActionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsRuleMoveAction));
        OnPropertyChanged(nameof(IsRuleTagAction));
    }

    partial void OnSelectedRuleChanged(MailRule? value)
    {
        OnPropertyChanged(nameof(HasSelectedRuleForRun));

        if (value is null) return;
        RuleName = value.Name;
        RuleFromContains = value.FromContains;
        RuleFromDomain = value.FromDomain;
        RuleSubjectContains = value.SubjectContains;
        RuleActionIndex = (int)value.Action;
        RuleTargetFolder = RuleFolders.FirstOrDefault(folder => folder.Id == value.TargetFolderId);
        RuleTag = RuleTags.FirstOrDefault(tag =>
            string.Equals(tag.Name, value.ActionArg, StringComparison.OrdinalIgnoreCase));
        RuleEnabled = value.Enabled;
        RuleContinueAfterMatch = value.ContinueAfterMatch;
    }

    /// <summary>「对已有邮件执行一次」要求先选中一条已保存的规则。</summary>
    public bool HasSelectedRuleForRun => SelectedRule is not null;

    /// <summary>
    /// 用选中的规则补跑一遍已有邮件：新规则只对之后入库的邮件生效，
    /// 这里给已经收到的邮件补一次整理（动作仍走本地优先 + 待同步队列）。
    /// </summary>
    [RelayCommand]
    private async Task RunRuleOnceAsync()
    {
        if (SelectedRule is not { } rule) return;

        var scopeChoice = _dialogs.Ask(new MessageDialogOptions
        {
            Title = "对已有邮件执行一次",
            Message = $"用规则「{rule.Name}」整理已经收到的邮件？",
            Detail = $"只处理收件箱：最多扫描最近 {RuleRunPlanner.InboxLimit} 封。\n"
                     + $"所有文件夹：跳过已发送 / 草稿 / 垃圾箱 / 已删除，最多 {RuleRunPlanner.AllFoldersLimit} 封。\n"
                     + (rule.Enabled ? "" : "提示：这条规则当前是停用状态，执行一次不会改动规则本身。"),
            Icon = DialogIcon.Question,
            PrimaryText = "只处理收件箱",
            SecondaryText = "所有文件夹",
            CancelText = "取消"
        });

        if (scopeChoice == DialogChoice.Cancel) return;

        var scope = scopeChoice == DialogChoice.Secondary ? RuleRunScope.AllFolders : RuleRunScope.Inbox;

        IsBusy = true;
        try
        {
            StatusText = "正在按规则整理已有邮件…";
            var progress = new Progress<RuleRunProgress>(p =>
                StatusText = $"已扫描 {p.Scanned} 封 · 命中 {p.Matched} 封 · {p.Stage}");

            var result = await _rules.RunOnceAsync(rule, scope, progress).ConfigureAwait(true);

            StatusText = result.Summary;

            // 先刷新列表（命中次数 / 最近命中时间），再弹结果框，用户关掉提示时看到的已是新数据
            await ReloadRulesAsync().ConfigureAwait(true);
            SelectedRule = Rules.FirstOrDefault(item => item.Id == rule.Id);

            _dialogs.Alert(
                "执行完成",
                result.Summary,
                result.Matched > 0 ? DialogIcon.Success : DialogIcon.Info,
                detail: rule.Enabled ? null : "这条规则仍是停用状态：只影响「新邮件入库」时是否自动执行。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "对已有邮件执行规则失败：{Rule}", rule.Name);
            StatusText = "执行失败：" + ex.Message;
            _dialogs.Alert("执行失败", ex.Message, DialogIcon.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void NewRule()
    {
        SelectedRule = null;
        RuleName = "";
        RuleFromContains = "";
        RuleFromDomain = "";
        RuleSubjectContains = "";
        RuleActionIndex = 0;
        RuleTargetFolder = RuleFolders.FirstOrDefault();
        RuleTag = RuleTags.FirstOrDefault();
        RuleEnabled = true;
        RuleContinueAfterMatch = false;
        StatusText = "填写条件与动作后点击“保存规则”";
    }

    [RelayCommand]
    private async Task SaveRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(RuleFromContains)
            && string.IsNullOrWhiteSpace(RuleFromDomain)
            && string.IsNullOrWhiteSpace(RuleSubjectContains))
        {
            StatusText = "至少填写一个条件（发件人包含 / 发件域包含 / 主题包含）";
            return;
        }
        if (RuleActionIndex == 0 && RuleTargetFolder is null)
        {
            StatusText = "请选择目标文件夹";
            return;
        }
        if (RuleActionIndex == 5 && RuleTag is null)
        {
            StatusText = "请选择要添加的标签（可先在「标签管理」新建）";
            return;
        }

        var rule = SelectedRule ?? new MailRule();
        rule.Name = string.IsNullOrWhiteSpace(RuleName)
            ? (string.IsNullOrWhiteSpace(RuleSubjectContains) ? RuleFromContains : RuleSubjectContains)
            : RuleName.Trim();
        rule.FromContains = RuleFromContains.Trim();
        rule.FromDomain = RuleFromDomain.Trim();
        rule.SubjectContains = RuleSubjectContains.Trim();
        rule.Action = (MailRuleAction)RuleActionIndex;
        rule.TargetFolderId = RuleActionIndex == 0 ? RuleTargetFolder?.Id : null;
        rule.ActionArg = RuleActionIndex == 5 ? RuleTag?.Name ?? string.Empty : string.Empty;
        rule.Enabled = RuleEnabled;
        rule.ContinueAfterMatch = RuleContinueAfterMatch;

        try
        {
            await _rules.SaveAsync(rule);
            await ReloadRulesAsync();
            SelectedRule = Rules.FirstOrDefault(item => item.Id == rule.Id);
            StatusText = "规则已保存";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存规则失败");
            StatusText = "保存规则失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is not { } rule) return;

        try
        {
            await _rules.DeleteAsync(rule.Id);
            await ReloadRulesAsync();
            NewRule();
            StatusText = "规则已删除";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除规则失败");
            StatusText = "删除规则失败：" + ex.Message;
        }
    }

    private async Task ReloadRulesAsync()
    {
        try
        {
            var rules = await _rules.GetAllAsync();
            Rules.Clear();
            foreach (var rule in rules) Rules.Add(rule);

            var tags = await _tags.GetAllAsync();
            RuleTags.Clear();
            foreach (var tag in tags) RuleTags.Add(tag);

            if (RuleFolders.Count == 0)
            {
                foreach (var account in Accounts)
                {
                    foreach (var folder in await _folders.GetByAccountAsync(account.Id))
                    {
                        if (folder.Kind is FolderKind.Sent or FolderKind.Drafts) continue;
                        RuleFolders.Add(folder);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载规则失败");
        }
    }

    // ---------------- 反垃圾（本地黑白名单） ----------------

    public ObservableCollection<SenderFilter> SenderFilters { get; } = new();

    public IReadOnlyList<string> SenderFilterKindOptions { get; } = new[] { "黑名单", "白名单" };

    [ObservableProperty] private string _filterPattern = "";
    [ObservableProperty] private string _filterNote = "";
    [ObservableProperty] private int _filterKindIndex;
    [ObservableProperty] private SenderFilter? _selectedSenderFilter;

    public bool IsBlacklistMode
    {
        get => FilterKindIndex == 0;
        set => FilterKindIndex = value ? 0 : 1;
    }

    public bool IsWhitelistMode
    {
        get => FilterKindIndex == 1;
        set => FilterKindIndex = value ? 1 : 0;
    }

    partial void OnFilterKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsBlacklistMode));
        OnPropertyChanged(nameof(IsWhitelistMode));
    }

    [RelayCommand]
    private async Task AddSenderFilterAsync()
    {
        var result = await _senderFilters.SaveAsync(FilterPattern, IsBlacklistMode, FilterNote);
        StatusText = result.Message;
        if (!result.Success) return;

        FilterPattern = "";
        FilterNote = "";
        await ReloadSenderFiltersAsync(forceRefresh: true);
        SelectedSenderFilter = SenderFilters.FirstOrDefault(item => item.Id == result.Filter?.Id);
    }

    [RelayCommand]
    private async Task DeleteSenderFilterAsync()
    {
        if (SelectedSenderFilter is not { } filter)
        {
            StatusText = "请先在上方列表中选择要删除的条目";
            return;
        }

        await _senderFilters.DeleteAsync(filter.Id);
        await ReloadSenderFiltersAsync(forceRefresh: true);
        SelectedSenderFilter = null;
        StatusText = $"已移除：{filter.Pattern}";
    }

    private async Task ReloadSenderFiltersAsync(bool forceRefresh = false)
    {
        try
        {
            var filters = await _senderFilters.GetAllAsync(forceRefresh);
            SenderFilters.Clear();
            foreach (var filter in filters) SenderFilters.Add(filter);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载发件人黑白名单失败");
            StatusText = "加载黑白名单失败：" + ex.Message;
        }
    }

    // ---------------- 标签管理（按账户隔离 + 单键快捷键） ----------------

    public ObservableCollection<MailTag> Tags { get; } = new();

    /// <summary>标签所属账户下拉的候选项（标签必须挂在某个账户下）。</summary>
    public ObservableCollection<Account> TagAccounts { get; } = new();

    [ObservableProperty] private Account? _selectedTagAccount;

    [ObservableProperty] private MailTag? _selectedTag;
    [ObservableProperty] private string _tagName = "";
    [ObservableProperty] private string _tagColor = "#2563eb";

    /// <summary>单键快捷键下拉的候选项：首项「未绑定」，其余为可绑定的单键。</summary>
    public IReadOnlyList<string> TagKeyOptions { get; } =
        new[] { UnboundTagKey }.Concat(TagShortcutKeys.All).ToList();

    private const string UnboundTagKey = "未绑定";

    /// <summary>当前选择的下拉项（索引 0 = 未绑定）。</summary>
    [ObservableProperty] private int _tagKeyIndex;

    /// <summary>首次填充账户下拉时不要再触发一次重载。</summary>
    private bool _tagAccountInitializing;

    partial void OnSelectedTagChanged(MailTag? value)
    {
        if (value is null) return;
        TagName = value.Name;
        TagColor = value.Color;
        TagKeyIndex = IndexOfTagKey(value.ShortcutKey);
    }

    partial void OnSelectedTagAccountChanged(Account? value)
    {
        if (value is null || _tagAccountInitializing) return;

        // 切换账户后重新加载该账户的标签，编辑框回到「新建」
        _ = ReloadTagsAsync();
        NewTag();
    }

    private static int IndexOfTagKey(string? key)
    {
        var normalized = TagShortcutKeys.Normalize(key);
        if (normalized is null) return 0;

        for (var i = 0; i < TagShortcutKeys.All.Length; i++)
        {
            if (string.Equals(TagShortcutKeys.All[i], normalized, StringComparison.OrdinalIgnoreCase)) return i + 1;
        }

        return 0;
    }

    /// <summary>下拉索引 → 键位（0 表示未绑定）。</summary>
    private string? SelectedTagKey
        => TagKeyIndex <= 0 || TagKeyIndex > TagShortcutKeys.All.Length
            ? null
            : TagShortcutKeys.All[TagKeyIndex - 1];

    [RelayCommand]
    private void NewTag()
    {
        SelectedTag = null;
        TagName = "";
        TagColor = "#2563eb";
        TagKeyIndex = 0;
        StatusText = "填写名称、选择单键后点击“保存标签”；选中邮件按该键即可打 / 取消标签";
    }

    [RelayCommand]
    private async Task SaveTagAsync()
    {
        if (SelectedTagAccount is not { } account)
        {
            StatusText = "请先选择标签所属账户";
            return;
        }

        var tag = SelectedTag ?? new MailTag { SortOrder = Tags.Count };
        tag.Name = TagName;
        tag.Color = string.IsNullOrWhiteSpace(TagColor) ? "#2563eb" : TagColor.Trim();
        tag.AccountId = account.Id;
        tag.ShortcutKey = SelectedTagKey;

        var (ok, error) = await _tags.SaveAsync(tag);
        if (!ok)
        {
            StatusText = error ?? "保存标签失败";
            return;
        }

        await ReloadTagsAsync();
        SelectedTag = Tags.FirstOrDefault(item => item.Id == tag.Id);
        StatusText = tag.ShortcutKey is { } key
            ? $"标签已保存：选中邮件后按 {key} 可打 / 取消"
            : "标签已保存（未绑定快捷键）";
    }

    [RelayCommand]
    private async Task DeleteTagAsync()
    {
        if (SelectedTag is not { } tag) return;

        var confirm = _dialogs.Confirm(
            "删除标签",
            $"删除标签「{tag.Name}」吗？",
            primaryText: "删除",
            detail: "已打该标签的邮件会同时移除该标签，邮件本身不受影响。",
            icon: DialogIcon.Warning,
            danger: true);
        if (!confirm) return;

        await _tags.DeleteAsync(tag.Id);
        await ReloadTagsAsync();
        NewTag();
        StatusText = "标签已删除";
    }

    /// <summary>加载账户下拉（只在进入标签分区时做一次）。</summary>
    private async Task EnsureTagAccountsAsync()
    {
        if (TagAccounts.Count > 0) return;

        try
        {
            var accounts = await _accounts.GetAllAsync();
            foreach (var account in accounts.OrderBy(a => a.SortOrder)) TagAccounts.Add(account);

            if (SelectedTagAccount is null)
            {
                _tagAccountInitializing = true;
                SelectedTagAccount = TagAccounts.FirstOrDefault();
                _tagAccountInitializing = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载标签账户失败");
        }
    }

    private async Task ReloadTagsAsync()
    {
        try
        {
            await EnsureTagAccountsAsync();

            var accountId = SelectedTagAccount?.Id ?? 0;
            var tags = accountId > 0
                ? await _tags.GetByAccountAsync(accountId)
                : Array.Empty<MailTag>();

            Tags.Clear();
            foreach (var tag in tags) Tags.Add(tag);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载标签失败");
        }
    }
    // ---------------- 数据导出 ----------------

    /// <summary>「数据」页的存储占用缓存：Tab 反复切换不重复扫表，清理 / 导入后强制重算。</summary>
    private long? _storageSizeBytes;

    /// <summary>正在后台统计占用：避免连续切换 Tab 叠加多次扫表。</summary>
    private bool _storageSizeLoading;

    private async Task RefreshStorageSizeAsync(bool force = false)
    {
        if (!force && _storageSizeBytes is { } cached)
        {
            StorageSize = DisplayFormat.FileSize(cached);
            return;
        }

        if (_storageSizeLoading) return;

        _storageSizeLoading = true;
        StorageSize = "统计中…";
        try
        {
            // SQLite 的「异步」接口底层仍是同步调用，而这条 SQL 要 SUM(length(body_html)) 扫全表正文，
            // 慢机器上能卡住 UI 几百毫秒。放到线程池算，切 Tab 时先把分区画出来。
            var bytes = await Task.Run(() => _messages.GetStorageSizeAsync()).ConfigureAwait(true);
            _storageSizeBytes = bytes;
            StorageSize = DisplayFormat.FileSize(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "计算存储占用失败");
            StorageSize = "未知";
        }
        finally
        {
            _storageSizeLoading = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出本地数据",
            FileName = $"ZungMail-backup-{DateTime.Now:yyyyMMdd}.zip",
            Filter = "ZIP 压缩包 (*.zip)|*.zip"
        };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        StatusText = "正在打包本地数据…";
        try
        {
            var path = await _exporter.ExportAsync(dialog.FileName);
            StatusText = "已导出到 " + path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出数据失败");
            StatusText = "导出失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>运行分阶段收发诊断，并展示可复制的报告。</summary>
    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        if (SelectedAccount is not { } account)
        {
            StatusText = "请先选择一个账户";
            return;
        }

        IsBusy = true;
        StatusText = "正在诊断…";
        try
        {
            var report = await _diagnostics.RunAsync(account.Id);
            StatusText = report.Success ? "诊断完成：全部通过" : "诊断完成：发现问题";
            _dialogs.ShowDiagnosticsReport(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "收发诊断失败");
            StatusText = "诊断失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
    /// <summary>重建全文索引：搜索异常或历史数据升级后使用，过程可观察。</summary>
    [RelayCommand]
    private async Task RebuildIndexAsync()
    {
        IsBusy = true;
        StatusText = "正在重建搜索索引…";
        try
        {
            var progress = new Progress<int>(count => StatusText = $"正在重建搜索索引… 已处理 {count} 封");
            var count = await _messages.RebuildSearchIndexAsync(progress);
            StatusText = $"搜索索引已重建，共处理 {count} 封邮件";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重建搜索索引失败");
            StatusText = "重建索引失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>立即清理正文与附件缓存（保留邮件头，需要时重新按需下载）。</summary>
    [RelayCommand]
    private async Task ClearBodyCacheAsync()
    {
        var confirm = _dialogs.Confirm(
            "清理正文缓存",
            "将清空本地已缓存的正文与附件。",
            primaryText: "清理缓存",
            detail: "邮件头会保留，阅读时重新下载。",
            icon: DialogIcon.Warning,
            danger: true);
        if (!confirm) return;

        IsBusy = true;
        try
        {
            var count = await _messages.ClearBodyCacheAsync();
            await RefreshStorageSizeAsync(force: true);
            StatusText = $"已清理 {count} 封邮件的正文缓存";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理正文缓存失败");
            StatusText = "清理失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenUserGuide()
    {
        if (UserGuide.TryOpen(AppContext.BaseDirectory, out var error))
        {
            StatusText = "已打开使用教程";
            return;
        }

        _logger.LogWarning("打开使用教程失败：{Error}", error);
        StatusText = error;
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        if (!Directory.Exists(DataDirectory)) return;

        try
        {
            Process.Start(new ProcessStartInfo(DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开数据目录失败：{Path}", DataDirectory);
            StatusText = "无法打开数据目录：" + ex.Message;
        }
    }

    // ---------------- 数据保存目录 ----------------

    private string _dataDirectoryDefault = "";
    private string _dataDirectoryPendingTarget = "";
    private string _dataDirectoryPendingSource = "";

    public string DataDirectoryDefaultText => "默认目录：" + _dataDirectoryDefault;

    public bool HasPendingDataMigration => _dataDirectoryPendingTarget.Length > 0;

    public string PendingDataMigrationText =>
        $"已排入下次启动：把数据迁移到 {_dataDirectoryPendingTarget}（源：{_dataDirectoryPendingSource}）";

    public string DataDirectoryHint => HasPendingDataMigration
        ? "迁移会复制数据到新目录，完成后旧目录保留作为备份；迁移前请勿删除当前目录。"
        : "改目录只保存一个位置指针，下次启动客户端时自动迁移；旧目录会保留。不支持网络盘与受保护目录。";

    [RelayCommand]
    private async Task BrowseDataDirectoryAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择数据保存目录",
            Multiselect = false
        };

        try { dialog.InitialDirectory = DataDirectory; }
        catch (Exception) { /* 目录不存在时用系统默认 */ }

        if (dialog.ShowDialog() != true) return;
        await ApplyDataDirectoryAsync(dialog.FolderName).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ResetDataDirectoryAsync()
    {
        await ApplyDataDirectoryAsync(_dataDirectoryDefault).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelPendingDataMigration()
    {
        try
        {
            _dataDirectories.CancelPending();
            RefreshDataDirectoryState();
            StatusText = "已取消待执行的数据迁移，仍使用当前目录";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取消数据迁移失败");
            _dialogs.Alert("取消失败", ex.Message, DialogIcon.Error);
        }
    }

    /// <summary>预检目标目录 → 用户确认 → 只写指针（下次启动迁移）。</summary>
    private async Task ApplyDataDirectoryAsync(string target)
    {
        try
        {
            var validation = await _dataDirectories.ValidateTargetAsync(target).ConfigureAwait(true);
            if (!validation.Ok)
            {
                _dialogs.Alert("无法使用该目录", validation.Message, DialogIcon.Warning);
                return;
            }

            var detail = "迁移只复制、不删除，原目录会保留作为备份；下次启动客户端时执行。";
            if (validation.WillBackupExisting)
            {
                detail = "目标目录里已有一份 ZungMail 数据，迁移前会自动改名备份。" + Environment.NewLine + detail;
            }

            var confirm = _dialogs.Confirm(
                "更换数据保存目录",
                "下次启动客户端时，会把当前数据迁移到：" + Environment.NewLine + target,
                "确认并等待重启",
                detail,
                DialogIcon.Question);

            if (!confirm) return;

            _dataDirectories.ScheduleMigration(target);
            RefreshDataDirectoryState();
            StatusText = "已保存新的数据目录，重新启动客户端后自动迁移";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置数据目录失败：{Target}", target);
            _dialogs.Alert("设置失败", ex.Message, DialogIcon.Error);
        }
    }

    /// <summary>把指针状态同步到界面（当前目录、待迁移目标、提示文案）。</summary>
    private void RefreshDataDirectoryState()
    {
        try
        {
            var status = _dataDirectories.GetStatus();
            _dataDirectoryDefault = status.DefaultDirectory;
            _dataDirectoryPendingTarget = status.PendingTarget ?? "";
            _dataDirectoryPendingSource = status.PendingSource ?? "";
            DataDirectory = status.CurrentDirectory;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取数据目录状态失败");
            DataDirectory = _paths.RootDirectory;
        }

        OnPropertyChanged(nameof(DataDirectoryDefaultText));
        OnPropertyChanged(nameof(HasPendingDataMigration));
        OnPropertyChanged(nameof(PendingDataMigrationText));
        OnPropertyChanged(nameof(DataDirectoryHint));
    }

    // ---------------- 存储与清理 ----------------

    /// <summary>存储上限以 GB 输入，0 表示按磁盘总容量计算占用比例。</summary>
    public int StorageLimitGb
    {
        get => (int)Math.Round(Settings.StorageLimitBytes / 1073741824.0);
        set
        {
            Settings.StorageLimitBytes = Math.Max(0, value) * 1073741824L;
            OnPropertyChanged();
        }
    }

    public string StorageLevelText => StorageSnapshot?.Level switch
    {
        StorageAlertLevel.Critical => "严重：空间即将耗尽",
        StorageAlertLevel.Warning => "警告：建议立即清理",
        StorageAlertLevel.Notice => "提示：接近阈值",
        _ => "正常"
    };

    public string StorageUsageText => StorageSnapshot is { } s
        ? $"{s.TotalDisplay}（数据库 {s.DatabaseDisplay} · 附件 {s.AttachmentDisplay} · 日志 {s.LogDisplay}）"
        : "统计中…";

    public string StorageDiskText => StorageSnapshot is { } s
        ? $"磁盘可用 {s.FreeDisplay} / {s.DriveTotalDisplay} · 已用 {s.UsageDisplay} · 可释放约 {s.ReclaimableDisplay}"
        : "";

    public string StorageCountText => StorageSnapshot is { } s
        ? $"{s.MessageCountDisplay} · 附件 {s.AttachmentCount:N0} 个"
        : "";

    public double StorageUsagePercent => StorageSnapshot?.UsagePercent ?? 0;

    public string StorageAlertText => StorageSnapshot?.AlertText ?? "";

    partial void OnStorageSnapshotChanged(StorageSnapshot? value)
    {
        OnPropertyChanged(nameof(StorageLevelText));
        OnPropertyChanged(nameof(StorageUsageText));
        OnPropertyChanged(nameof(StorageDiskText));
        OnPropertyChanged(nameof(StorageCountText));
        OnPropertyChanged(nameof(StorageUsagePercent));
        OnPropertyChanged(nameof(StorageAlertText));
    }

    private async Task RefreshStorageAsync()
    {
        try
        {
            // 快照里有多条聚合 SQL，SQLite 的异步接口底层仍同步执行；放到线程池，
            // 切到「存储与清理」时先画出分区再填数字，慢机器上不会卡住界面。
            var snapshot = await Task.Run(() => _storageMonitor.GetSnapshotAsync()).ConfigureAwait(true);
            StorageSnapshot = snapshot;
            StorageSize = DisplayFormat.FileSize(snapshot.TotalBytes);
            _storageSizeBytes = snapshot.TotalBytes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取存储快照失败");
            StatusText = "统计存储占用失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshStorageCommandAsync() => await RefreshStorageAsync();

    [RelayCommand]
    private async Task OpenCleanupAsync()
    {
        _dialogs.ShowCleanup();
        await RefreshStorageAsync();
    }

    [RelayCommand]
    private async Task PurgeExpiredAsync()
    {
        IsBusy = true;
        try
        {
            var freed = await _cleanup.PurgeExpiredAsync();
            await RefreshStorageAsync();
            StatusText = freed > 0
                ? $"已清理超期隔离数据，释放 {DisplayFormat.FileSize(freed)}"
                : "没有超过保留期的隔离数据";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理超期隔离数据失败");
            StatusText = "清理失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>模板编辑区的三种视图状态。</summary>
public enum TemplateViewMode
{
    /// <summary>仅编辑。</summary>
    Edit,

    /// <summary>左侧编辑 + 右侧实时预览。</summary>
    Split,

    /// <summary>仅预览（所见即所得）。</summary>
    Preview
}

/// <summary>可视化内容块：标题、按钮文案与要插入的 HTML。</summary>
public sealed record TemplateBlock(string Title, string Hint, string Html);

/// <summary>
/// 签名页的账户条目：账户基本信息 + 是否启用签名 + 富文本签名 HTML。
/// 编辑时先写在这份包装对象上，点「保存设置」再统一写回 <see cref="AppSettings.Signatures"/>。
/// </summary>
public sealed partial class SignatureAccountItem : ObservableObject
{
    public SignatureAccountItem(Account account, AccountSignature? entry, string fallbackHtml)
    {
        AccountId = account.Id;
        Name = string.IsNullOrWhiteSpace(account.Name) ? account.Email : account.Name;
        Email = account.Email;
        Initials = account.Initials;
        Color = account.Color;

        IsEnabled = entry?.IsEnabled ?? !string.IsNullOrWhiteSpace(fallbackHtml);
        Html = entry?.Html ?? fallbackHtml;
    }

    public long AccountId { get; }
    public string Name { get; }
    public string Email { get; }
    public string Initials { get; }
    public string Color { get; }

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _html = "";

    public string StateText => IsEnabled ? "启用" : "关闭";

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(StateText));
}

/// <summary>
/// 模板列表项：包装领域模型，补充缩略图、分组展示与「未保存」标记。
/// 领域模型不实现通知，列表的实时更新由本包装类负责。
/// </summary>
public sealed partial class TemplateListItem : ObservableObject
{
    private string[] _thumbnail;

    public TemplateListItem(MailTemplate model)
    {
        Model = model;
        Snapshot = model.Clone();
        _thumbnail = BuildThumbnailLines(model.Body);
    }

    public MailTemplate Model { get; }

    /// <summary>最近一次成功保存时的内容快照，用于判断是否有未保存修改。</summary>
    public MailTemplate Snapshot { get; set; }

    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? "未命名模板" : Model.Name;

    /// <summary>快速文本没有主题，列表副标题改为正文预览，避免出现一堆「（无主题）」。</summary>
    public string SubjectDisplay => Model.IsQuickText
        ? HtmlTextExtractor.BuildPreview(Model.Body, 60, isHtml: false)
        : Model.SubjectDisplay;

    public string CategoryDisplay =>
        string.IsNullOrWhiteSpace(Model.Category) ? MailTemplate.DefaultCategory : Model.Category;

    /// <summary>供列表缩略图提取纯文本摘要。</summary>
    public string Body => Model.Body;

    public bool IsBuiltIn => Model.IsBuiltIn;

    public bool IsQuickText => Model.IsQuickText;

    // ---- 列表缩略预览：取正文前三行文本，以极小字号画出「一页纸」的轮廓 ----

    public string ThumbnailLine1 => ThumbnailLine(0);
    public string ThumbnailLine2 => ThumbnailLine(1);
    public string ThumbnailLine3 => ThumbnailLine(2);
    public bool HasThumbnailText => _thumbnail.Length > 0;

    [ObservableProperty] private bool _isDirty;

    public bool MatchesSnapshot() =>
        string.Equals(Model.Name ?? "", Snapshot.Name ?? "", StringComparison.Ordinal) &&
        string.Equals(Model.Subject ?? "", Snapshot.Subject ?? "", StringComparison.Ordinal) &&
        string.Equals(Model.Body ?? "", Snapshot.Body ?? "", StringComparison.Ordinal) &&
        string.Equals(Model.Description ?? "", Snapshot.Description ?? "", StringComparison.Ordinal) &&
        string.Equals(Model.Category ?? "", Snapshot.Category ?? "", StringComparison.Ordinal) &&
        Model.IsQuickText == Snapshot.IsQuickText;

    public void Refresh()
    {
        IsDirty = !MatchesSnapshot();
        _thumbnail = BuildThumbnailLines(Model.Body);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(SubjectDisplay));
        OnPropertyChanged(nameof(CategoryDisplay));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(IsBuiltIn));
        OnPropertyChanged(nameof(IsQuickText));
        OnPropertyChanged(nameof(ThumbnailLine1));
        OnPropertyChanged(nameof(ThumbnailLine2));
        OnPropertyChanged(nameof(ThumbnailLine3));
        OnPropertyChanged(nameof(HasThumbnailText));
    }

    private string ThumbnailLine(int index) => index < _thumbnail.Length ? _thumbnail[index] : "";

    private static string[] BuildThumbnailLines(string? body)
    {
        var text = HtmlTextExtractor.ToPlainText(body ?? "");
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var lines = new List<string>(3);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            lines.Add(line.Length > 10 ? line[..10] + "…" : line);
            if (lines.Count == 3) break;
        }

        return lines.ToArray();
    }
}

/// <summary>需要用户填写取值的占位符（如 {主题}、{地点}）。</summary>
public sealed partial class TemplatePlaceholderEntry : ObservableObject
{
    public TemplatePlaceholderEntry(string token, string label)
    {
        Token = token;
        Label = label;
    }

    public string Token { get; }

    public string Label { get; }

    [ObservableProperty] private string _value = "";
}

