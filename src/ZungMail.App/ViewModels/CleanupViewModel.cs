using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>清理预览行（支持手动勾选）。</summary>
public sealed partial class CleanupItemRow : ObservableObject
{
    public CleanupItemRow(CleanupPreviewItem item) => Item = item;

    public CleanupPreviewItem Item { get; }

    [ObservableProperty]
    private bool _isSelected;

    public long Id => Item.Id;
    public string SubjectDisplay => Item.SubjectDisplay;
    public string FolderName => Item.FolderName;
    public string FromEmail => Item.FromEmail;
    public string DateDisplay => Item.DateDisplay;
    public string SizeDisplay => Item.SizeDisplay;
    public bool IsStarred => Item.IsStarred;
    public bool IsUnread => Item.IsUnread;
}

/// <summary>
/// 清理列表的勾选集合：跨页保存「哪些邮件被勾了」，供单页全选 / 反选与整批全选 / 反选使用。
/// 只存 id，二十万级也只是一串 long。
/// </summary>
public sealed class CleanupSelection
{
    private readonly HashSet<long> _ids = new();

    public int Count => _ids.Count;

    public bool Contains(long id) => _ids.Contains(id);

    public void Set(long id, bool selected)
    {
        if (selected) _ids.Add(id);
        else _ids.Remove(id);
    }

    /// <summary>整体勾选一批（全选）：以本批为准，清掉上一轮筛选留下的 id，避免误删范围外的邮件。</summary>
    public void SelectAll(IEnumerable<long> ids)
    {
        _ids.Clear();
        _ids.UnionWith(ids);
    }

    /// <summary>反选整批：原来勾上的取消，原来没勾的勾上（不在本批里的 id 一律取消，避免残留）。</summary>
    public void InvertAll(IEnumerable<long> ids)
    {
        var inverted = new HashSet<long>();
        foreach (var id in ids)
        {
            if (!_ids.Contains(id)) inverted.Add(id);
        }

        _ids.Clear();
        _ids.UnionWith(inverted);
    }

    public void Clear() => _ids.Clear();

    /// <summary>按当前预览顺序导出，保证清理顺序与列表一致。</summary>
    public List<long> Snapshot() => _ids.ToList();
}

/// <summary>
/// 历史邮件清理：多维度筛选预览、一键 / 勾选 / 自动清理、进度、中断恢复、
/// 隔离期撤销与彻底删除。所有操作均落在审计表，可追溯。
/// </summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    /// <summary>单页条数：分页加载，避免十万级匹配一次性塞进列表。</summary>
    public const int PageSize = 50;

    private readonly ICleanupService _cleanup;
    private readonly IStorageMonitor _monitor;
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<CleanupViewModel> _logger;
    private readonly IProgress<CleanupProgress> _progress;

    private CancellationTokenSource? _cts;

    public CleanupViewModel(
        ICleanupService cleanup,
        IStorageMonitor monitor,
        ISettingsStore settings,
        IDialogService dialogs,
        ILogger<CleanupViewModel> logger)
    {
        _cleanup = cleanup;
        _monitor = monitor;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;
        _progress = new Progress<CleanupProgress>(OnProgress);

        BeforeDate = DateTime.Today;
    }

    public event EventHandler<bool>? CloseRequested;

    // ---------------- 筛选条件 ----------------

    [ObservableProperty] private bool _includeTrash = true;
    [ObservableProperty] private bool _includeJunk = true;
    [ObservableProperty] private bool _includeArchive;
    [ObservableProperty] private bool _includeInbox;
    [ObservableProperty] private bool _includeSent;

    [ObservableProperty] private DateTime? _beforeDate;
    [ObservableProperty] private DateTime? _afterDate;
    [ObservableProperty] private string _minSizeMb = "";
    [ObservableProperty] private string _maxSizeMb = "";
    [ObservableProperty] private bool _onlyWithAttachments;
    [ObservableProperty] private string _extensions = "";

    [ObservableProperty] private bool _protectStarred = true;
    [ObservableProperty] private bool _protectUnread = true;

    /// <summary>
    /// 仅清理正文与附件：命中邮件保留在列表里，只清空正文并删掉附件文件，
    /// 阅读时按需重新下载；不进入隔离区，因此清理记录里不可撤销。
    /// </summary>
    [ObservableProperty] private bool _clearBodyOnly;

    /// <summary>主按钮文案随模式变化，避免「一键清理」在仅清正文时被误解为删邮件。</summary>
    public string CleanupByFilterText => ClearBodyOnly ? "清理正文" : "一键清理";

    public string CleanupSelectedText => ClearBodyOnly ? "清理选中正文" : "清理选中";

    partial void OnClearBodyOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(CleanupByFilterText));
        OnPropertyChanged(nameof(CleanupSelectedText));

        // 匹配数量不变，但「预计释放」口径变了：已有预览就按新模式翻新当前页（保留勾选）
        if (HasPreview) _ = LoadPageAsync(_pageIndex);
    }

    // ---------------- 预览与进度 ----------------

    public ObservableCollection<CleanupItemRow> PreviewItems { get; } = new();

    public ObservableCollection<CleanupJob> Jobs { get; } = new();

    [ObservableProperty] private CleanupJob? _selectedJob;
    [ObservableProperty] private StorageSnapshot? _storage;
    [ObservableProperty] private int _previewCount;
    [ObservableProperty] private string _previewSize = "";
    [ObservableProperty] private int _selectedRows;

    /// <summary>跨页勾选集合：翻页 / 全选 / 反选都只动这一份。</summary>
    private readonly CleanupSelection _selection = new();

    private int _pageIndex;

    /// <summary>当前列表用的筛选条件：「全选 / 反选」以它为范围，避免筛选改了没预览导致勾到看不见的邮件。</summary>
    private CleanupFilter? _previewFilter;

    /// <summary>匹配结果总页数（至少 1 页，方便显示「第 1 / 1 页」）。</summary>
    [ObservableProperty] private int _pageCount = 1;

    public int CurrentPage => _pageIndex + 1;

    public string PageInfo => $"第 {CurrentPage} / {PageCount} 页";

    public bool CanGoPrevious => !IsRunning && _pageIndex > 0;

    public bool CanGoNext => !IsRunning && _pageIndex + 1 < PageCount;

    /// <summary>选择类按钮的统一可用性：有匹配结果且没有任务在跑。</summary>
    public bool CanSelect => PreviewCount > 0 && !IsRunning;

    /// <summary>「清理选中」只在真的勾了邮件时可用，避免空点。</summary>
    public bool CanCleanupSelected => _selection.Count > 0 && !IsRunning;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private double _progressPercent;

    /// <summary>预设作用域文案（「账户「工作邮箱」」/「文件夹「客户-欧洲」」）；空表示不限。</summary>
    [ObservableProperty] private string _scopeLabel = "";

    private long? _scopeAccountId;
    private long? _scopeFolderId;

    /// <summary>是否带作用域（界面上显示作用域条与「清除」按钮）。</summary>
    public bool HasScope => !string.IsNullOrEmpty(ScopeLabel);

    /// <summary>
    /// 应用预设作用域（从账户 / 文件夹右键进入时调用）。
    /// 只限制「清理哪一批邮件」，其余筛选条件仍按界面上的来。
    /// </summary>
    public void ApplyScope(CleanupScope? scope)
    {
        _scopeAccountId = scope?.AccountId;
        _scopeFolderId = scope?.FolderId;
        ScopeLabel = scope?.Label ?? string.Empty;
        OnPropertyChanged(nameof(HasScope));
    }

    /// <summary>清除作用域，回到「全账户」范围。</summary>
    [RelayCommand]
    private async Task ClearScopeAsync()
    {
        ApplyScope(null);
        await PreviewAsync();
    }

    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _statusText = "就绪";

    public string StorageSummary => Storage is null
        ? "正在统计本地占用…"
        : $"邮件数据 {Storage.TotalDisplay}（数据库 {Storage.DatabaseDisplay} · 附件 {Storage.AttachmentDisplay}）"
          + $" · 磁盘可用 {Storage.FreeDisplay} / {Storage.DriveTotalDisplay}"
          + $" · 可释放约 {Storage.ReclaimableDisplay}";

    public bool HasPreview => PreviewCount > 0;

    public string QuarantineHint =>
        $"隔离区保留 {_settings.Current.CleanupQuarantineDays} 天，期间可随时撤销；超期后自动彻底删除。";

    // ---------------- 初始化 ----------------

    public async Task InitializeAsync()
    {
        await RefreshStorageAsync();
        await ReloadJobsAsync();
        await PreviewAsync();
    }

    /// <summary>窗口关闭时停止进行中的任务，任务会以此状态落库为「已中断」，可稍后恢复。</summary>
    public void Detach()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* 已释放 */ }
    }

    partial void OnSelectedJobChanged(CleanupJob? value)
    {
        UndoCommand.NotifyCanExecuteChanged();
        PurgeCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    partial void OnStorageChanged(StorageSnapshot? value) => OnPropertyChanged(nameof(StorageSummary));

    partial void OnIsRunningChanged(bool value)
    {
        ResumeCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        PurgeCommand.NotifyCanExecuteChanged();
        CleanupByFilterCommand.NotifyCanExecuteChanged();
        CleanupSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(CanCleanupSelected));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    partial void OnPreviewCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(CanSelect));
    }

    private void OnProgress(CleanupProgress progress)
    {
        ProgressPercent = progress.Percent;
        ProgressText = progress.Text;
    }

    // ---------------- 预览 ----------------

    private CleanupFilter BuildFilter() => new()
    {
        // 作用域（账户 / 文件夹）来自右键入口，界面上的「清除」会把它复位
        AccountId = _scopeAccountId,
        FolderId = _scopeFolderId,
        IncludeInbox = IncludeInbox,
        IncludeSent = IncludeSent,
        IncludeTrash = IncludeTrash,
        IncludeJunk = IncludeJunk,
        IncludeArchive = IncludeArchive,
        // 含「早于该日」语义：选 2026-01-01 表示清理该日及以前收到的邮件
        BeforeUtc = BeforeDate is { } before
            ? new DateTimeOffset(before.Date.AddDays(1).AddSeconds(-1)).ToUniversalTime()
            : null,
        AfterUtc = AfterDate is { } after
            ? new DateTimeOffset(after.Date).ToUniversalTime()
            : null,
        MinSizeBytes = ParseMb(MinSizeMb),
        MaxSizeBytes = ParseMb(MaxSizeMb),
        OnlyWithAttachments = OnlyWithAttachments,
        Extensions = ParseExtensions(Extensions),
        ProtectStarred = ProtectStarred,
        ProtectUnread = ProtectUnread,
        ClearBodyOnly = ClearBodyOnly
    };

    private static long ParseMb(string text) =>
        double.TryParse(text, out var mb) && mb > 0 ? (long)(mb * 1024 * 1024) : 0;

    private static List<string> ParseExtensions(string text) => text
        .Split(new[] { ',', '，', ';', '；', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
        .Where(e => e.Length > 0 && e.All(char.IsLetterOrDigit))
        .Distinct()
        .ToList();

    /// <summary>按当前筛选条件重新预览：回到第一页并清空勾选（条件变了，旧的勾选不再可信）。</summary>
    [RelayCommand]
    private async Task PreviewAsync()
    {
        _selection.Clear();
        NotifySelectionChanged();
        _previewSummaryKey = "";                 // 数据可能变了（刚清理完/撤销），强制重算汇总
        await LoadPageAsync(0);
    }

    /// <summary>上一次汇总对应的筛选条件与数字：翻页时直接复用，避免每页都跑 COUNT/SUM。</summary>
    private string _previewSummaryKey = "";
    private int _previewTotal;
    private long _previewTotalBytes;
    private long _previewAttachmentBytes;
    private long _previewReclaimableBytes;

    /// <summary>加载指定页（0 基）。勾选集合跨页保留，行勾选状态由集合回填。</summary>
    private async Task LoadPageAsync(int pageIndex)
    {
        if (IsPreviewing || IsRunning) return;

        IsPreviewing = true;
        try
        {
            var index = Math.Max(0, pageIndex);
            var filter = BuildFilter();
            var offset = index * PageSize;
            var key = BuildPreviewKey(filter);

            IReadOnlyList<CleanupPreviewItem> items;
            int total;

            // SQLite 的「异步」底层同步执行：预览查询放线程池，界面线程只做行对象与属性赋值
            if (key != _previewSummaryKey)
            {
                var preview = await Task.Run(() => _cleanup.PreviewAsync(filter, PageSize, offset))
                    .ConfigureAwait(true);
                _previewSummaryKey = key;
                _previewTotal = preview.TotalCount;
                _previewTotalBytes = preview.TotalBytes;
                _previewAttachmentBytes = preview.AttachmentBytes;
                _previewReclaimableBytes = preview.ReclaimableBytes;
                items = preview.Items;
                total = preview.TotalCount;
            }
            else
            {
                items = await Task.Run(() => _cleanup.PreviewItemsAsync(filter, PageSize, offset))
                    .ConfigureAwait(true);
                total = _previewTotal;
            }

            _previewFilter = filter;
            PreviewCount = total;
            PageCount = Math.Max(1, (total + PageSize - 1) / PageSize);
            _pageIndex = Math.Min(index, PageCount - 1);

            PreviewItems.Clear();
            foreach (var item in items)
            {
                PreviewItems.Add(new CleanupItemRow(item) { IsSelected = _selection.Contains(item.Id) });
            }

            PreviewSize = ClearBodyOnly
                ? DisplayFormat.FileSize(_previewReclaimableBytes)
                : DisplayFormat.FileSize(_previewTotalBytes + _previewAttachmentBytes);
            NotifyPageChanged();
            NotifySelectionChanged();

            StatusText = total == 0
                ? "当前条件没有匹配的邮件"
                : (ClearBodyOnly
                    ? $"匹配 {total:N0} 封，预计清空正文与附件 {PreviewSize}"
                    : $"匹配 {total:N0} 封，合计 {PreviewSize}")
                  + (PageCount > 1 ? $"（第 {CurrentPage} / {PageCount} 页）" : "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理预览失败");
            StatusText = "预览失败：" + ex.Message;
        }
        finally
        {
            IsPreviewing = false;
        }
    }

    /// <summary>汇总缓存的键：任一筛选条件变化都要重算 COUNT/SUM。</summary>
    private static string BuildPreviewKey(CleanupFilter filter) => string.Join('|',
        filter.AccountId, filter.FolderId,
        filter.IncludeTrash, filter.IncludeJunk, filter.IncludeArchive, filter.IncludeInbox, filter.IncludeSent,
        filter.BeforeUtc?.ToUnixTimeSeconds(), filter.AfterUtc?.ToUnixTimeSeconds(),
        filter.MinSizeBytes, filter.MaxSizeBytes, filter.OnlyWithAttachments,
        string.Join(',', filter.Extensions), filter.ProtectStarred, filter.ProtectUnread, filter.ClearBodyOnly);

    private void NotifyPageChanged()
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    // ---------------- 勾选：单页 / 整批 ----------------

    /// <summary>单行勾选（界面点击）：只改这一条，不回读整页状态，避免虚拟化容器的空跑事件写歪集合。</summary>
    public void SetRowSelected(CleanupItemRow row, bool selected)
    {
        row.IsSelected = selected;
        _selection.Set(row.Id, selected);
        NotifySelectionChanged();
    }

    /// <summary>把当前页行的勾选状态重设为集合里的值（全选 / 反选后调用，不触发清理）。</summary>
    private void SyncPageSelection()
    {
        foreach (var row in PreviewItems)
        {
            row.IsSelected = _selection.Contains(row.Id);
        }
    }

    private void NotifySelectionChanged()
    {
        SelectedRows = _selection.Count;
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(CanCleanupSelected));
        CleanupSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectPage()
    {
        foreach (var row in PreviewItems)
        {
            row.IsSelected = true;
            _selection.Set(row.Id, true);
        }

        NotifySelectionChanged();
    }

    [RelayCommand]
    private void InvertPage()
    {
        foreach (var row in PreviewItems)
        {
            var next = !_selection.Contains(row.Id);
            row.IsSelected = next;
            _selection.Set(row.Id, next);
        }

        NotifySelectionChanged();
    }

    /// <summary>全选当前筛选下的全部邮件（跨页），只取 id，不加载列表。</summary>
    [RelayCommand]
    private async Task SelectAllAsync()
    {
        if (IsRunning) return;

        try
        {
            var ids = await _cleanup.GetMatchingIdsAsync(_previewFilter ?? BuildFilter());
            _selection.SelectAll(ids);
            SyncPageSelection();
            NotifySelectionChanged();
            StatusText = $"已勾选全部 {_selection.Count:N0} 封（共 {PageCount} 页），点「{CleanupSelectedText}」执行";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全选匹配邮件失败");
            StatusText = "全选失败：" + ex.Message;
        }
    }

    /// <summary>反选当前筛选下的全部邮件：原来勾上的取消，没勾的勾上。</summary>
    [RelayCommand]
    private async Task InvertAllAsync()
    {
        if (IsRunning) return;

        try
        {
            var ids = await _cleanup.GetMatchingIdsAsync(_previewFilter ?? BuildFilter());
            _selection.InvertAll(ids);
            SyncPageSelection();
            NotifySelectionChanged();
            StatusText = _selection.Count == 0
                ? "已反选：当前没有勾选的邮件"
                : $"已反选，勾选 {_selection.Count:N0} 封";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "反选匹配邮件失败");
            StatusText = "反选失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private Task FirstPageAsync() => LoadPageAsync(0);

    [RelayCommand]
    private Task PreviousPageAsync() => LoadPageAsync(_pageIndex - 1);

    [RelayCommand]
    private Task NextPageAsync() => LoadPageAsync(_pageIndex + 1);

    [RelayCommand]
    private Task LastPageAsync() => LoadPageAsync(PageCount - 1);

    // ---------------- 执行 ----------------

    [RelayCommand]
    private async Task CleanupByFilterAsync()
    {
        if (IsRunning) return;

        var filter = BuildFilter();
        var bodyOnly = ClearBodyOnly;
        var confirm = _dialogs.Confirm(
            bodyOnly ? "清理正文与附件" : "一键清理",
            bodyOnly ? "确定按当前筛选条件清空正文与附件吗？" : "确定按当前筛选条件清理邮件吗？",
            primaryText: bodyOnly ? "开始清理正文" : "开始清理",
            detail: (HasScope ? $"作用域：{ScopeLabel}\n" : string.Empty)
                    + $"{filter.Describe()}\n"
                    + $"文件夹：{DescribeFolders(filter)}\n"
                    + $"匹配数量：{PreviewCount:N0} 封（{(bodyOnly ? "可清空约" : "约")} {PreviewSize}）\n"
                    + (bodyOnly
                        ? "邮件本身保留，只清空已下载正文并删除附件文件，阅读时重新下载。\n此模式不进入隔离区，不可撤销。"
                        : "清理后进入隔离区，保留期内可一键撤销。"),
            icon: DialogIcon.Warning,
            danger: true);
        if (!confirm) return;

        await RunAsync(
            async ct => (CleanupJob?)await _cleanup.ExecuteAsync(filter, null, CleanupMode.Filter, _progress, ct),
            bodyOnly ? "清理正文" : "清理");
    }

    [RelayCommand(CanExecute = nameof(CanCleanupSelectedCommand))]
    private async Task CleanupSelectedAsync()
    {
        if (IsRunning) return;

        var ids = _selection.Snapshot();
        if (ids.Count == 0)
        {
            StatusText = "请先在列表中勾选要清理的邮件";
            return;
        }

        var bodyOnly = ClearBodyOnly;
        var confirm = _dialogs.Confirm(
            bodyOnly ? "清理选中的正文" : "清理选中",
            bodyOnly
                ? $"确定清空选中的 {ids.Count:N0} 封邮件的正文与附件吗？"
                : $"确定清理选中的 {ids.Count:N0} 封邮件吗？",
            primaryText: bodyOnly ? "开始清理正文" : "开始清理",
            detail: $"勾选可以跨页，本次共 {ids.Count:N0} 封（匹配总数 {PreviewCount:N0} 封）。\n"
                    + (bodyOnly
                        ? "邮件本身保留，阅读时重新下载。此模式不进入隔离区，不可撤销。"
                        : "清理后进入隔离区，保留期内可一键撤销。"),
            icon: DialogIcon.Warning,
            danger: true);
        if (!confirm) return;

        // 筛选条件在 UI 线程先算好，别让后台线程去读界面属性
        var filter = BuildFilter();
        await RunAsync(
            async ct => (CleanupJob?)await _cleanup.ExecuteAsync(
                filter, ids, CleanupMode.Selected, _progress, ct),
            bodyOnly ? "清理选中正文" : "清理选中");
    }

    /// <summary>「清理选中」只在真的勾了邮件时可用（命令侧门禁，界面另有 IsEnabled 绑定）。</summary>
    private bool CanCleanupSelectedCommand() => _selection.Count > 0 && !IsRunning;

    private async Task RunAsync(
        Func<CancellationToken, Task<CleanupJob?>> action, string verb)
    {
        IsRunning = true;
        ProgressPercent = 0;
        ProgressText = "正在准备…";
        _cts = new CancellationTokenSource();

        try
        {
            // 清理是「逐批 SQL + 文件移动」的重活，必须离开界面线程：否则进度回调排不上队、窗口假死
            var job = await Task.Run(() => action(_cts.Token), _cts.Token).ConfigureAwait(true);
            if (job is null)
            {
                StatusText = $"{verb}未执行：没有匹配的邮件";
            }
            else if (job.Status == CleanupStatus.Cancelled)
            {
                StatusText = $"已中断，可稍后在记录中恢复：{job.SummaryDisplay}";
            }
            else
            {
                StatusText = $"{verb}完成：{job.SummaryDisplay}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已中断，进度已保存，可在记录中恢复";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Verb}失败", verb);
            StatusText = $"{verb}失败：" + ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
            ProgressText = "";
            await RefreshStorageAsync();
            await ReloadJobsAsync();
            await PreviewAsync();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cts is null) return;
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* 已完成 */ }
        StatusText = "正在中断，等待当前批次落库…";
    }

    // ---------------- 审计记录 ----------------

    private async Task ReloadJobsAsync()
    {
        try
        {
            var jobs = await _cleanup.GetJobsAsync(50);
            var selectedId = SelectedJob?.Id;

            Jobs.Clear();
            foreach (var job in jobs) Jobs.Add(job);

            SelectedJob = selectedId is { } id ? Jobs.FirstOrDefault(j => j.Id == id) : Jobs.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取清理记录失败");
        }
    }

    [RelayCommand]
    private async Task RefreshJobsAsync()
    {
        await ReloadJobsAsync();
        await RefreshStorageAsync();
        StatusText = "记录已刷新";
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeAsync()
    {
        if (SelectedJob is not { } job || IsRunning) return;

        await RunAsync(ct => _cleanup.ResumeAsync(job.Id, _progress, ct), "恢复清理");
    }

    private bool CanResume() => SelectedJob is { CanResume: true } && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (SelectedJob is not { } job || IsRunning) return;

        var confirm = _dialogs.Confirm(
            "撤销清理",
            $"确定把任务 #{job.Id} 中的邮件恢复到原文件夹吗？",
            primaryText: "撤销清理",
            detail: $"将恢复 {job.ProcessedCount:N0} 封邮件，并还原对应附件。",
            icon: DialogIcon.Question);
        if (!confirm) return;

        IsRunning = true;
        StatusText = "正在恢复邮件…";
        try
        {
            var restored = await _cleanup.UndoAsync(job.Id);
            StatusText = $"已恢复 {restored:N0} 封邮件";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "撤销清理失败：{JobId}", job.Id);
            StatusText = "撤销失败：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            await RefreshStorageAsync();
            await ReloadJobsAsync();
            await PreviewAsync();
        }
    }

    private bool CanUndo() => SelectedJob is { CanUndo: true } && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanPurge))]
    private async Task PurgeAsync()
    {
        if (SelectedJob is not { } job || IsRunning) return;

        var confirm = _dialogs.Confirm(
            "彻底删除",
            $"确定彻底删除任务 #{job.Id} 的隔离区数据吗？",
            primaryText: "彻底删除",
            detail: $"邮件：{job.ProcessedCount:N0} 封\n"
                    + $"预计释放：{DisplayFormat.FileSize(job.TotalBytes)}\n"
                    + "此操作不可撤销。",
            icon: DialogIcon.Warning,
            danger: true);
        if (!confirm) return;

        IsRunning = true;
        StatusText = "正在彻底删除…";
        try
        {
            var freed = await _cleanup.PurgeAsync(job.Id);
            StatusText = $"已释放 {DisplayFormat.FileSize(freed)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "彻底删除失败：{JobId}", job.Id);
            StatusText = "彻底删除失败：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            await RefreshStorageAsync();
            await ReloadJobsAsync();
        }
    }

    private bool CanPurge() => SelectedJob is { CanPurge: true } && !IsRunning;

    [RelayCommand]
    private async Task PurgeExpiredAsync()
    {
        if (IsRunning) return;

        IsRunning = true;
        StatusText = "正在清理超期隔离数据…";
        try
        {
            var freed = await _cleanup.PurgeExpiredAsync();
            StatusText = freed > 0
                ? $"已释放 {DisplayFormat.FileSize(freed)}"
                : "没有超过保留期的隔离数据";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理超期隔离数据失败");
            StatusText = "清理失败：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            await RefreshStorageAsync();
            await ReloadJobsAsync();
        }
    }

    // ---------------- 存储快照 ----------------

    private async Task RefreshStorageAsync()
    {
        try
        {
            Storage = await _monitor.GetSnapshotAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取存储快照失败");
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, true);

    private static string DescribeFolders(CleanupFilter filter)
    {
        var names = new List<string>();
        if (filter.IncludeInbox) names.Add("收件箱");
        if (filter.IncludeSent) names.Add("已发送");
        if (filter.IncludeTrash) names.Add("垃圾箱");
        if (filter.IncludeJunk) names.Add("广告邮件");
        if (filter.IncludeArchive) names.Add("归档");
        return names.Count == 0 ? "未选择（不会清理任何邮件）" : string.Join(" / ", names);
    }
}
