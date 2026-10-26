using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ZungMail.App.ViewModels;

/// <summary>待发送附件（仅承载路径，供界面显示文件名与大小）。</summary>
public sealed class ComposeAttachment
{
    public ComposeAttachment(string path) => Path = path;

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>附件大小；文件已被移动或删除时返回空字符串，不打断撰写界面。</summary>
    public string SizeDisplay
    {
        get
        {
            try
            {
                var length = new FileInfo(Path).Length;
                return length > 0 ? DisplayFormat.FileSize(length) : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}

/// <summary>
/// 撰写 / 回复 / 转发（FR-07 / FR-08）：收件人 T9 补全（FR-10）、模板套用、
/// 附件、定时发送；邮件统一进入发件箱后再由 <see cref="IOutboxProcessor"/> 投递并归档「已发送」。
/// 侧栏提供快速文本与地址簿（双击追加，不覆盖已写内容）。
/// </summary>
public sealed partial class ComposeViewModel : ObservableObject
{
    /// <summary>快速文本分类下拉中「全部」的固定取值。</summary>
    public const string AllQuickTextCategories = "全部";

    private readonly IAccountRepository _accounts;
    private readonly IOutboxRepository _outbox;
    private readonly IOutboxProcessor _processor;
    private readonly IContactRepository _contacts;
    private readonly ITemplateStore _templates;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly IComposeDraftStore _drafts;
    private readonly IMailDraftService _draftMails;
    private readonly ILogger<ComposeViewModel> _logger;
    private readonly ComposeRequest _request;

    private readonly List<MailTemplate> _quickTextSource = new();
    private readonly List<Contact> _contactSource = new();

    private int _suggestVersion;

    /// <summary>本地草稿 id：首次自动保存时生成，之后覆盖同一份。</summary>
    private string? _draftId;

    /// <summary>上次保存时的内容指纹，用来判断有没有变化。</summary>
    private string _draftSignature = "";

    /// <summary>草稿箱里这封草稿的本地邮件 id：再次保存覆盖同一封，发送成功后删掉。</summary>
    private long? _draftMailId;

    private System.Windows.Threading.DispatcherTimer? _autoSaveTimer;

    /// <summary>构造期从设置恢复侧栏状态时置位，避免把恢复动作本身再写回设置。</summary>
    private bool _suppressSidebarPersist;

    public ComposeViewModel(
        IAccountRepository accounts,
        IOutboxRepository outbox,
        IOutboxProcessor processor,
        IContactRepository contacts,
        ITemplateStore templates,
        ISettingsStore settings,
        IDialogService dialogs,
        IComposeDraftStore drafts,
        IMailDraftService draftMails,
        ThemeManager theme,
        ILogger<ComposeViewModel> logger,
        ComposeRequest request)
    {
        _accounts = accounts;
        _outbox = outbox;
        _processor = processor;
        _contacts = contacts;
        _templates = templates;
        _settings = settings;
        _dialogs = dialogs;
        _drafts = drafts;
        _draftMails = draftMails;
        _logger = logger;
        _request = request;
        _draftMailId = request.DraftEmailId;

        // 快速文本侧栏沿用上一次的开关状态；首次使用仍是关闭
        _suppressSidebarPersist = true;
        IsQuickTextPanelVisible = _settings.Current.ComposeQuickTextPanelVisible;
        _suppressSidebarPersist = false;

        To = request.To;
        Cc = request.Cc;
        Bcc = request.Bcc;
        Subject = request.Subject;
        // 打开草稿时如果带了抄送，直接把抄送行展开，避免用户以为内容丢了
        ShowCcBcc = !string.IsNullOrWhiteSpace(request.Cc) || !string.IsNullOrWhiteSpace(request.Bcc);
        BodyHtml = ComposeBodyBuilder.ToEditorHtml(request.Body);
        IsDark = theme.IsDark;
        foreach (var path in request.Attachments) Attachments.Add(new ComposeAttachment(path));
    }

    public string Title => _request.Mode switch
    {
        ComposeMode.Reply => "回复",
        ComposeMode.ReplyAll => "回复全部",
        ComposeMode.Forward => "转发",
        _ => "写邮件"
    };

    public ObservableCollection<Account> Accounts { get; } = new();

    public ObservableCollection<ComposeAttachment> Attachments { get; } = new();

    public ObservableCollection<MailTemplate> Templates { get; } = new();

    /// <summary>侧栏快速文本（按当前分类过滤后的结果）。</summary>
    public ObservableCollection<MailTemplate> QuickTexts { get; } = new();

    /// <summary>快速文本分类下拉：首项为「全部」，其余按数据首次出现顺序排列。</summary>
    public ObservableCollection<string> QuickTextCategories { get; } = new();

    /// <summary>侧栏地址簿（按过滤词筛选后的结果）。</summary>
    public ObservableCollection<Contact> Contacts { get; } = new();

    public ObservableCollection<Contact> Suggestions { get; } = new();

    public event EventHandler<bool>? CloseRequested;

    /// <summary>需要把文本插入正文光标处；由撰写窗口转交 <see cref="Controls.RichTextEditor"/>。</summary>
    public event EventHandler<string>? QuickTextInsertRequested;

    [ObservableProperty] private Account? _selectedAccount;
    [ObservableProperty] private string _to = "";
    [ObservableProperty] private string _cc = "";
    [ObservableProperty] private string _bcc = "";
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _bodyHtml = "";
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private MailTemplate? _selectedTemplate;
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private DateTime? _scheduleDate = DateTime.Today;
    [ObservableProperty] private string _scheduleTime = "09:00";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private bool _showCcBcc;
    /// <summary>当前发件账户的签名 HTML，编辑器会在正文末尾实时展示；切账户即替换。</summary>
    [ObservableProperty] private string _signatureHtml = "";
    [ObservableProperty] private bool _isQuickTextPanelVisible;
    [ObservableProperty] private bool _isQuickTextTabActive = true;
    [ObservableProperty] private string _selectedQuickTextCategory = AllQuickTextCategories;
    [ObservableProperty] private string _contactFilter = "";

    /// <summary>
    /// 上次未发送的草稿：不再用模态确认框逼用户当场选，改成编辑区顶部一张可忽略的卡片，
    /// 想恢复再点「恢复到编辑器」。
    /// </summary>
    private ComposeDraft? _pendingDraft;

    /// <summary>草稿恢复卡片是否显示（打开写信窗口时发现草稿即为 true，用户处理后收起）。</summary>
    [ObservableProperty] private bool _showDraftRecoveryCard;

    /// <summary>卡片上的说明：保存时间 + 主题 + 附件情况。</summary>
    [ObservableProperty] private string _draftRecoveryDescription = "";

    /// <summary>侧栏「地址簿」Tab 是否选中；写入 true 时切回地址簿。</summary>
    public bool IsAddressBookTabActive
    {
        get => !IsQuickTextTabActive;
        set
        {
            if (value && IsQuickTextTabActive) IsQuickTextTabActive = false;
        }
    }

    /// <summary>侧栏是否展示空状态提示。</summary>
    public bool IsQuickTextListEmpty => QuickTexts.Count == 0;

    public bool IsContactListEmpty => Contacts.Count == 0;

    public string QuickTextSummary => $"{_quickTextSource.Count} 条";

    public string ContactSummary => $"{_contactSource.Count} 位联系人";

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>供编辑器「插入附件标识」弹层使用的文件名列表。</summary>
    public IReadOnlyList<string> AttachmentNames => Attachments.Select(a => a.FileName).ToArray();

    public bool HasSuggestions => Suggestions.Count > 0;

    public async Task InitializeAsync()
    {
        try
        {
            var accounts = await _accounts.GetAllAsync();
            foreach (var account in accounts.Where(a => a.IsEnabled)) Accounts.Add(account);

            SelectedAccount = _request.AccountId is { } id
                ? Accounts.FirstOrDefault(a => a.Id == id) ?? Accounts.FirstOrDefault()
                : Accounts.FirstOrDefault();

            if (Accounts.Count == 0)
            {
                StatusText = "尚未配置可用的邮箱账户，请先在「设置 → 账户」中添加。";
            }

            var templates = await _templates.GetAllAsync();
            foreach (var template in templates.Where(t => !t.IsQuickText)) Templates.Add(template);

            // 快速文本与整封模板共用一份存储，这里按标记分流到侧栏
            _quickTextSource.Clear();
            _quickTextSource.AddRange(templates.Where(t => t.IsQuickText));
            RebuildQuickTextCategories();
            ApplyQuickTextFilter();
            OnPropertyChanged(nameof(QuickTextSummary));

            var contacts = await _contacts.GetAllAsync();
            _contactSource.Clear();
            _contactSource.AddRange(contacts.Where(c => !string.IsNullOrWhiteSpace(c.Email)));
            ApplyContactFilter();
            OnPropertyChanged(nameof(ContactSummary));

            // 草稿恢复只针对「新写」；回复/转发有自己的引用正文，不能被旧草稿覆盖；
            // 从草稿箱打开的那封草稿已经带着内容，不再弹恢复提示
            if (_request.Mode == ComposeMode.New && _draftMailId is null) await TryRestoreDraftAsync();
            StartAutoSave();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化撰写窗口失败");
            StatusText = "初始化失败：" + ex.Message;
        }
    }

    // ---------------- 本地草稿（自动保存 / 崩溃恢复） ----------------

    /// <summary>每 30 秒检查一次；内容有变化且非空才落盘。</summary>
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(30);

    /// <summary>当前窗口里是否有值得保存的内容（全空时关闭不弹「保存草稿」）。</summary>
    public bool HasDraftContent
    {
        get
        {
            var draft = BuildDraft();
            return draft.HasContent();
        }
    }

    private ComposeDraft BuildDraft() => new()
    {
        Id = _draftId ?? Guid.NewGuid().ToString("N"),
        Mode = _request.Mode.ToString(),
        AccountId = SelectedAccount?.Id ?? _request.AccountId,
        To = To,
        Cc = Cc,
        Bcc = Bcc,
        Subject = Subject,
        BodyHtml = BodyHtml,
        Attachments = Attachments.Select(a => a.Path).ToList(),
        InReplyTo = _request.InReplyTo,
        References = _request.References
    };

    /// <summary>内容指纹：判断自上次保存以来有没有改动，避免每 30 秒重复写盘。</summary>
    private string BuildSignature()
        => string.Join(
            '\u0001',
            To, Cc, Bcc, Subject,
            BodyHtml.Length.ToString(CultureInfo.InvariantCulture),
            BodyHtml.GetHashCode().ToString(CultureInfo.InvariantCulture),
            Attachments.Count.ToString(CultureInfo.InvariantCulture));

    /// <summary>保存草稿（内容为空时什么都不做）。返回是否真的写了盘。</summary>
    public async Task<bool> SaveDraftAsync(bool onlyIfChanged = false)
    {
        try
        {
            var draft = BuildDraft();
            if (!draft.HasContent())
            {
                // 用户把内容清空了：顺手删掉旧草稿，避免下次又弹恢复
                if (_draftId is not null)
                {
                    await _drafts.DeleteAsync(_draftId).ConfigureAwait(true);
                    _draftId = null;
                    _draftSignature = "";
                }

                return false;
            }

            var signature = BuildSignature();
            if (onlyIfChanged && signature == _draftSignature) return false;

            _draftId ??= draft.Id;
            draft.Id = _draftId;
            await _drafts.SaveAsync(draft).ConfigureAwait(true);
            _draftSignature = signature;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存草稿失败");
            return false;
        }
    }

    /// <summary>放弃草稿（发送成功或用户选择「放弃」时调用）。</summary>
    public async Task DiscardDraftAsync()
    {
        await DeleteDraftMailAsync().ConfigureAwait(true);

        if (_draftId is null) return;

        try
        {
            await _drafts.DeleteAsync(_draftId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除草稿失败");
        }
        finally
        {
            _draftId = null;
            _draftSignature = "";
        }
    }

    // ---------------- 草稿箱（真正落一封草稿邮件） ----------------

    /// <summary>
    /// 把当前内容写进草稿箱：本地先落库（立刻可见），后台再 APPEND 到服务器草稿箱。
    /// 与关窗恢复用的本机 JSON 草稿并存——JSON 负责「崩溃恢复」，草稿邮件负责「在草稿箱里能看到、能继续编辑」。
    /// </summary>
    public async Task SaveDraftMailAsync()
    {
        if (SelectedAccount is null) return;

        var draft = BuildDraft();
        if (!draft.HasContent()) return;

        try
        {
            var payload = new DraftMailPayload(
                SelectedAccount.Id,
                To,
                Cc,
                Bcc,
                Subject,
                BodyHtml,
                Attachments.Select(a => a.Path).ToList(),
                _request.InReplyTo,
                _request.References);

            var id = await _draftMails.SaveAsync(payload, _draftMailId).ConfigureAwait(true);
            if (id is { } saved)
            {
                _draftMailId = saved;
                StatusText = "草稿已保存到草稿箱";
                DraftSaved?.Invoke(this, saved);
            }
            else
            {
                StatusText = "草稿已保存在本机（未找到草稿箱文件夹）";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存草稿邮件失败");
            StatusText = "保存草稿失败：" + ex.Message;
        }
    }

    /// <summary>草稿邮件写入成功（主窗口可据此刷新草稿箱列表）。</summary>
    public event EventHandler<long>? DraftSaved;

    /// <summary>界面上的「保存草稿 / Ctrl+S」：本机 JSON 与草稿箱各存一份，不关窗。</summary>
    [RelayCommand]
    private async Task SaveDraftToBoxAsync()
    {
        await SaveDraftAsync().ConfigureAwait(true);
        await SaveDraftMailAsync().ConfigureAwait(true);
    }

    private async Task DeleteDraftMailAsync()
    {
        if (_draftMailId is not { } id) return;

        try
        {
            await _draftMails.DeleteAsync(id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除草稿邮件失败");
        }
        finally
        {
            _draftMailId = null;
        }
    }

    /// <summary>
    /// 打开写信窗口时找一找上一次未发送的草稿：只把卡片显示出来，不动用户正在写的正文，
    /// 要不要恢复由用户自己点（原来是模态确认框，必须先选一个才能继续）。
    /// </summary>
    private async Task TryRestoreDraftAsync()
    {
        try
        {
            var drafts = await _drafts.GetAllAsync().ConfigureAwait(true);
            var latest = drafts.FirstOrDefault();
            if (latest is null) return;

            _pendingDraft = latest;

            var savedAt = DisplayFormat.FullDate(latest.UpdatedUtc);
            var subject = string.IsNullOrWhiteSpace(latest.Subject) ? "（无主题）" : latest.Subject;
            var attachments = latest.Attachments.Count == 0 ? "" : $" · {latest.Attachments.Count} 个附件";
            DraftRecoveryDescription = $"{savedAt} 保存{attachments} · {subject}";
            ShowDraftRecoveryCard = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查找上次草稿失败");
        }
    }

    /// <summary>把草稿恢复到编辑器（点卡片上的「恢复到编辑器」）。</summary>
    [RelayCommand]
    private void RestoreRecoveredDraft()
    {
        if (_pendingDraft is not { } latest) return;

        To = latest.To;
        Cc = latest.Cc;
        Bcc = latest.Bcc;
        Subject = latest.Subject;
        BodyHtml = latest.BodyHtml;
        if (!string.IsNullOrWhiteSpace(latest.Cc) || !string.IsNullOrWhiteSpace(latest.Bcc)) ShowCcBcc = true;

        Attachments.Clear();
        var missing = 0;
        foreach (var path in latest.Attachments)
        {
            if (File.Exists(path)) Attachments.Add(new ComposeAttachment(path));
            else missing++;
        }

        _draftId = latest.Id;
        _draftSignature = BuildSignature();
        _pendingDraft = null;
        ShowDraftRecoveryCard = false;
        StatusText = missing == 0
            ? "已恢复上次未发送的草稿"
            : $"已恢复草稿，其中 {missing} 个附件已不存在，请重新添加";
    }

    /// <summary>不恢复这份草稿：收起卡片，草稿仍留在本机（下次打开写信窗口还会提示）。</summary>
    [RelayCommand]
    private void DismissDraftRecovery()
    {
        _pendingDraft = null;
        ShowDraftRecoveryCard = false;
        StatusText = "已从空白开始写；上次的草稿仍保存在本机";
    }

    private void StartAutoSave()
    {
        if (_autoSaveTimer is not null) return;

        _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = AutoSaveInterval
        };
        _autoSaveTimer.Tick += async (_, _) => await SaveDraftAsync(onlyIfChanged: true).ConfigureAwait(true);
        _autoSaveTimer.Start();
    }

    /// <summary>窗口关闭时停掉定时器（窗口关闭后 VM 不再需要写盘）。</summary>
    public void StopAutoSave()
    {
        _autoSaveTimer?.Stop();
        _autoSaveTimer = null;
    }

    // ---------------- 签名（按发件账户实时展示） ----------------

    /// <summary>发件账户变化时换成该账户的签名；编辑器负责把它贴在正文末尾。</summary>
    partial void OnSelectedAccountChanged(Account? value)
        => SignatureHtml = value is null ? string.Empty : _settings.Current.ResolveSignatureHtml(value.Id);

    // ---------------- 侧栏：快速文本 / 地址簿 ----------------

    [RelayCommand]
    private void ToggleQuickTextPanel() => IsQuickTextPanelVisible = !IsQuickTextPanelVisible;

    [RelayCommand]
    private void ShowQuickTextTab() => IsQuickTextTabActive = true;

    [RelayCommand]
    private void ShowAddressBookTab() => IsQuickTextTabActive = false;

    /// <summary>打开设置页模板管理（快速文本与整封模板在同一处维护）。</summary>
    [RelayCommand]
    private void ManageQuickTexts() => _dialogs.ShowSettings("templates");

    partial void OnIsQuickTextTabActiveChanged(bool value)
        => OnPropertyChanged(nameof(IsAddressBookTabActive));

    /// <summary>侧栏开关变化时写回设置，下次打开写信窗口沿用这次的状态。</summary>
    partial void OnIsQuickTextPanelVisibleChanged(bool value)
    {
        if (_suppressSidebarPersist) return;

        _settings.Current.ComposeQuickTextPanelVisible = value;
        _ = PersistSidebarStateAsync();
    }

    private async Task PersistSidebarStateAsync()
    {
        try
        {
            await _settings.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 记住开关只是便利功能，写盘失败不影响正在写的邮件
            _logger.LogWarning(ex, "保存快速文本侧栏状态失败");
        }
    }

    partial void OnSelectedQuickTextCategoryChanged(string value) => ApplyQuickTextFilter();

    partial void OnContactFilterChanged(string value) => ApplyContactFilter();

    /// <summary>双击快速文本：把话术交给编辑器追加到光标处，不覆盖已写内容。</summary>
    [RelayCommand]
    private void InsertQuickText(MailTemplate? quick)
    {
        if (quick is null) return;

        // 正文既可能是纯文本，也可能是在设置页里用 HTML 写的，按有无标签分别处理
        var text = (quick.Body ?? string.Empty)
            .Contains('<') ? HtmlTextExtractor.ToPlainText(quick.Body) : (quick.Body ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            StatusText = $"「{quick.Name}」内容为空，请在「设置 → 模板」中补充";
            return;
        }

        QuickTextInsertRequested?.Invoke(this, text);
        StatusText = $"已插入快速文本「{quick.Name}」";
    }

    /// <summary>双击联系人：追加到收件人，已有同一地址时不重复添加。</summary>
    [RelayCommand]
    private void InsertContact(Contact? contact)
    {
        if (contact is null || string.IsNullOrWhiteSpace(contact.Email)) return;

        var parts = To.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (parts.Any(p => p.Contains(contact.Email, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = $"{contact.Display} 已在收件人中";
            return;
        }

        parts.Add(contact.Display);
        To = string.Join(", ", parts) + ", ";
        StatusText = $"已添加收件人 {contact.Display}";
    }

    private void RebuildQuickTextCategories()
    {
        QuickTextCategories.Clear();
        QuickTextCategories.Add(AllQuickTextCategories);

        foreach (var category in _quickTextSource
                     .Select(q => string.IsNullOrWhiteSpace(q.Category) ? MailTemplate.DefaultCategory : q.Category)
                     .Distinct(StringComparer.Ordinal))
        {
            QuickTextCategories.Add(category);
        }

        if (!QuickTextCategories.Contains(SelectedQuickTextCategory))
        {
            SelectedQuickTextCategory = AllQuickTextCategories;
        }
    }

    private void ApplyQuickTextFilter()
    {
        var category = SelectedQuickTextCategory;
        var filterAll = string.IsNullOrEmpty(category) || category == AllQuickTextCategories;

        QuickTexts.Clear();
        foreach (var quick in _quickTextSource)
        {
            var quickCategory = string.IsNullOrWhiteSpace(quick.Category)
                ? MailTemplate.DefaultCategory
                : quick.Category;
            if (!filterAll && !string.Equals(quickCategory, category, StringComparison.Ordinal)) continue;
            QuickTexts.Add(quick);
        }

        OnPropertyChanged(nameof(IsQuickTextListEmpty));
    }

    private void ApplyContactFilter()
    {
        var filter = (ContactFilter ?? string.Empty).Trim();

        Contacts.Clear();
        foreach (var contact in _contactSource)
        {
            if (filter.Length == 0 ||
                contact.Email.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (contact.Name ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Contacts.Add(contact);
            }
        }

        OnPropertyChanged(nameof(IsContactListEmpty));
    }

    // ---------------- 收件人补全 ----------------

    /// <summary>收件人补全的防抖间隔：连打一串字符只查最后一次。</summary>
    private const int SuggestDebounceMs = 120;

    private CancellationTokenSource? _suggestDebounce;

    partial void OnToChanged(string value) => QueueSuggest(value);

    /// <summary>输入停顿 120ms 再查库；新输入取消上一次未完成查询（不再逐键查 2300 联系人）。</summary>
    private void QueueSuggest(string value)
    {
        _suggestDebounce?.Cancel();
        _suggestDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _suggestDebounce = cts;

        FireAndForget.Run(async () =>
        {
            try
            {
                await Task.Delay(SuggestDebounceMs, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await SuggestAsync(value).ConfigureAwait(true);
        }, "收件人补全");
    }

    private async Task SuggestAsync(string value)
    {
        var version = ++_suggestVersion;
        var prefix = LastToken(value);
        if (prefix.Length < 1)
        {
            Suggestions.Clear();
            OnPropertyChanged(nameof(HasSuggestions));
            return;
        }

        try
        {
            // SQLite 的「异步」底层同步执行：查询放线程池，结果回 UI
            var matches = await Task.Run(() => _contacts.SuggestAsync(prefix, 6)).ConfigureAwait(true);
            if (version != _suggestVersion) return;

            Suggestions.Clear();
            foreach (var contact in matches) Suggestions.Add(contact);
            OnPropertyChanged(nameof(HasSuggestions));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "联系人补全失败");
        }
    }

    [RelayCommand]
    private void PickSuggestion(Contact? contact)
    {
        if (contact is null) return;

        var parts = To.Split(new[] { ',', ';' }).Select(p => p.Trim()).ToList();
        if (parts.Count == 0) parts.Add("");
        parts[^1] = contact.Display;

        To = string.Join(", ", parts.Where(p => p.Length > 0)) + ", ";
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
    }

    private static string LastToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var index = value.LastIndexOfAny(new[] { ',', ';' });
        var token = index < 0 ? value : value[(index + 1)..];
        return token.Trim();
    }

    // ---------------- 附件与模板 ----------------

    [RelayCommand]
    private void AddAttachment()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择附件" };
        if (dialog.ShowDialog() != true) return;

        AddAttachments(dialog.FileNames);
    }

    /// <summary>把附件路径加入附件条（工具栏按钮与编辑器「插入附件标识」共用）。</summary>
    public void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var file in paths)
        {
            if (Attachments.Any(a => string.Equals(a.Path, file, StringComparison.OrdinalIgnoreCase))) continue;
            Attachments.Add(new ComposeAttachment(file));
        }

        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentNames));
    }

    [RelayCommand]
    private void RemoveAttachment(ComposeAttachment? attachment)
    {
        if (attachment is null) return;
        Attachments.Remove(attachment);
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(AttachmentNames));
    }

    [RelayCommand]
    private void ApplyTemplate()
    {
        if (SelectedTemplate is not { } template) return;

        Subject = template.Subject;
        BodyHtml = ComposeBodyBuilder.ToEditorHtml(template.Body);
        StatusText = $"已套用模板「{template.Name}」";
    }

    [RelayCommand]
    private void ToggleCcBcc() => ShowCcBcc = !ShowCcBcc;

    // ---------------- 延迟发送 ----------------

    /// <summary>延迟 N 分钟后发送：直接写入定时字段，复用既有 ScheduledUtc 流程。</summary>
    private void DelayBy(TimeSpan span)
    {
        var when = DateTime.Now.Add(span);
        ScheduleEnabled = true;
        ScheduleDate = when.Date;
        ScheduleTime = when.ToString("HH:mm", CultureInfo.InvariantCulture);
        StatusText = $"将于 {DisplayFormat.FullDate(new DateTimeOffset(when))} 发送";
    }

    [RelayCommand]
    private void DelayOneMinute() => DelayBy(TimeSpan.FromMinutes(1));

    [RelayCommand]
    private void DelayThirtyMinutes() => DelayBy(TimeSpan.FromMinutes(30));

    [RelayCommand]
    private void DelayTomorrowMorning()
    {
        var when = DateTime.Today.AddDays(1).AddHours(9);
        ScheduleEnabled = true;
        ScheduleDate = when.Date;
        ScheduleTime = "09:00";
        StatusText = $"将于 {DisplayFormat.FullDate(new DateTimeOffset(when))} 发送";
    }

    // ---------------- 发送 ----------------

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsSending) return;

        if (SelectedAccount is not { } account)
        {
            StatusText = "请选择发件账户";
            return;
        }
        if (!account.HasCompleteConfig)
        {
            StatusText = "所选账户配置不完整，请先在设置中补全";
            return;
        }

        var to = ParseAddresses(To);
        var cc = ParseAddresses(Cc);
        var bcc = ParseAddresses(Bcc);
        if (to.Count + cc.Count + bcc.Count == 0)
        {
            StatusText = "请至少填写一位收件人";
            return;
        }

        // 发送前检查：漏附件、空主题这类问题只提示不阻断，用户确认后照常发送。
        // 设置 → 通用里可以整体关掉（关掉后完全不弹确认框，适合已经习惯自己检查的用户）。
        var warnings = _settings.Current.SendCheckEnabled
            ? ComposeSendGuard.Check(new ComposeCheckInput(
                Subject,
                BodyHtml,
                Attachments.Count,
                to.Count + cc.Count + bcc.Count,
                _request.Mode is ComposeMode.Reply or ComposeMode.ReplyAll))
            : Array.Empty<ComposeWarning>();

        if (warnings.Count > 0)
        {
            var proceed = _dialogs.Confirm(
                "发送前检查",
                $"有 {warnings.Count} 项需要确认",
                "仍然发送",
                string.Join(Environment.NewLine, warnings.Select(w => "· " + w.Message)),
                DialogIcon.Warning);

            if (!proceed)
            {
                StatusText = "已取消发送，请补全后再发";
                return;
            }
        }

        DateTimeOffset? scheduled = null;
        if (ScheduleEnabled)
        {
            if (!TryResolveSchedule(out var when, out var error))
            {
                StatusText = error;
                return;
            }
            scheduled = when;
        }

        var item = new OutboxItem
        {
            AccountId = account.Id,
            MessageId = OutboxItem.NewMessageId(account.Email),
            To = to,
            Cc = cc,
            Bcc = bcc,
            Subject = Subject.Trim(),
            BodyHtml = BuildBody(account),
            IsHtml = true,
            ScheduledUtc = scheduled,
            InReplyTo = _request.InReplyTo,
            References = _request.References
        };
        item.AttachmentPaths.AddRange(Attachments.Select(a => a.Path));

        IsSending = true;
        StatusText = "正在加入发件箱…";
        try
        {
            await _outbox.AddAsync(item);

            // 入队就算「用户这边完成」：清草稿、立刻关窗，投递交给发件箱的后台线程。
            // 以前这里 await ProcessDueAsync() 等 SMTP 发完才关窗——发一封带附件的邮件
            // 要等几秒到几十秒，窗口关不掉，用户以为卡死。
            // 结果（成功 / 失败重试）由托盘气泡与主窗口状态栏反馈，失败件保留在发件箱可重试。
            await DiscardDraftAsync().ConfigureAwait(true);

            // 窗口马上关，这句是「关窗那一刻」的最终状态，也方便日志/测试核对
            StatusText = scheduled is { } when2
                ? $"已加入发件箱，将于 {DisplayFormat.FullDate(when2)} 发送"
                : "已加入发件箱，正在后台发送";

            CloseRequested?.Invoke(this, true);

            if (scheduled is null)
            {
                // 立刻踢一脚后台投递，不等它返回：马上发的信不用干等 20 秒的调度轮询
                FireAndForget.Run(() => _processor.ProcessDueAsync(), "立即投递发件箱");
            }
        }
        catch (Exception ex)
        {
            // 只有「连发件箱都写不进去」才留在窗口里（数据库异常 / 磁盘满）
            _logger.LogError(ex, "加入发件箱失败：{Subject}", item.Subject);
            StatusText = "无法加入发件箱：" + ex.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    /// <summary>按发件账户追加它自己的富文本签名（未启用则为空，不追加）。</summary>
    private string BuildBody(Account account)
        => ComposeBodyBuilder.AppendSignatureHtml(
            BodyHtml,
            _settings.Current.ResolveSignatureHtml(account.Id));

    private bool TryResolveSchedule(out DateTimeOffset when, out string error)
    {
        when = default;
        var date = ScheduleDate ?? DateTime.Today;

        if (!TimeSpan.TryParseExact(ScheduleTime?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var time))
        {
            error = "定时时间格式应为 HH:mm，例如 09:30";
            return false;
        }

        var local = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Local);
        when = new DateTimeOffset(local);

        if (when <= DateTimeOffset.Now)
        {
            error = "定时发送时间需晚于当前时间";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>解析「显示名 &lt;地址&gt;」或裸地址，支持 , 与 ; 分隔。</summary>
    private static List<EmailAddress> ParseAddresses(string raw)
    {
        var result = new List<EmailAddress>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var token in raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = token.Trim();
            if (part.Length == 0) continue;

            var angle = part.LastIndexOf('<');
            if (angle >= 0 && part.EndsWith('>'))
            {
                var name = part[..angle].Trim().Trim('"');
                var address = part[(angle + 1)..^1].Trim();
                if (address.Length > 0) result.Add(new EmailAddress(address, name));
            }
            else if (part.Contains('@'))
            {
                result.Add(new EmailAddress(part, ""));
            }
        }
        return result;
    }
}
