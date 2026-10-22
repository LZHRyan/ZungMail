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
/// 历史邮件清理：多维度筛选预览、一键 / 勾选 / 自动清理、进度、中断恢复、
/// 隔离期撤销与彻底删除。所有操作均落在审计表，可追溯。
/// </summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    /// <summary>预览列表上限：百万级下也只取前 N 封，避免一次性载入。</summary>
    private const int PreviewLimit = 500;

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

    // ---------------- 预览与进度 ----------------

    public ObservableCollection<CleanupItemRow> PreviewItems { get; } = new();

    public ObservableCollection<CleanupJob> Jobs { get; } = new();

    [ObservableProperty] private CleanupJob? _selectedJob;
    [ObservableProperty] private StorageSnapshot? _storage;
    [ObservableProperty] private int _previewCount;
    [ObservableProperty] private string _previewSize = "";
    [ObservableProperty] private int _selectedRows;

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

    partial void OnPreviewCountChanged(int value) => OnPropertyChanged(nameof(HasPreview));

    partial void OnIsRunningChanged(bool value)
    {
        ResumeCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        PurgeCommand.NotifyCanExecuteChanged();
        CleanupByFilterCommand.NotifyCanExecuteChanged();
        CleanupSelectedCommand.NotifyCanExecuteChanged();
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
        ProtectUnread = ProtectUnread
    };

    private static long ParseMb(string text) =>
        double.TryParse(text, out var mb) && mb > 0 ? (long)(mb * 1024 * 1024) : 0;

    private static List<string> ParseExtensions(string text) => text
        .Split(new[] { ',', '，', ';', '；', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
        .Where(e => e.Length > 0 && e.All(char.IsLetterOrDigit))
        .Distinct()
        .ToList();

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (IsPreviewing || IsRunning) return;

        IsPreviewing = true;
        try
        {
            var preview = await _cleanup.PreviewAsync(BuildFilter(), PreviewLimit);
            PreviewItems.Clear();
            foreach (var item in preview.Items) PreviewItems.Add(new CleanupItemRow(item));

            PreviewCount = preview.TotalCount;
            PreviewSize = preview.TotalSizeDisplay;
            RecalculateSelection();
            StatusText = preview.TotalCount == 0
                ? "当前条件没有匹配的邮件"
                : $"匹配 {preview.TotalCount:N0} 封，合计 {preview.TotalSizeDisplay}";
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

    /// <summary>预览行勾选变化后刷新汇总（由界面在勾选时调用）。</summary>
    public void RecalculateSelection()
    {
        SelectedRows = PreviewItems.Count(r => r.IsSelected);
    }

    // ---------------- 执行 ----------------

    [RelayCommand]
    private async Task CleanupByFilterAsync()
    {
        if (IsRunning) return;

        var filter = BuildFilter();
        var confirm = _dialogs.Confirm(
            "一键清理",
            "确定按当前筛选条件清理邮件吗？",
            primaryText: "开始清理",
            detail: (HasScope ? $"作用域：{ScopeLabel}\n" : string.Empty)
                    + $"{filter.Describe()}\n"
                    + $"文件夹：{DescribeFolders(filter)}\n"
                    + $"匹配数量：{PreviewCount:N0} 封（约 {PreviewSize}）\n"
                    + "清理后进入隔离区，保留期内可一键撤销。",
            icon: DialogIcon.Warning,
            danger: true);
        if (!confirm) return;

        await RunAsync(
            async ct => (CleanupJob?)await _cleanup.ExecuteAsync(filter, null, CleanupMode.Filter, _progress, ct),
            "清理");
    }

    [RelayCommand]
    private async Task CleanupSelectedAsync()
    {
        if (IsRunning) return;

        var ids = PreviewItems.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (ids.Count == 0)
        {
            StatusText = "请先在列表中勾选要清理的邮件";
            return;
        }

        var confirm = _dialogs.Confirm(
            "勾选清理",
            $"确定清理手动勾选的 {ids.Count:N0} 封邮件吗？",
            primaryText: "开始清理",
            detail: $"列表展示上限 {PreviewLimit} 封。\n清理后进入隔离区，保留期内可一键撤销。",
            icon: DialogIcon.Warning,
            danger: true);
        if (!confirm) return;

        await RunAsync(
            async ct => (CleanupJob?)await _cleanup.ExecuteAsync(
                BuildFilter(), ids, CleanupMode.Selected, _progress, ct),
            "勾选清理");
    }

    private async Task RunAsync(
        Func<CancellationToken, Task<CleanupJob?>> action, string verb)
    {
        IsRunning = true;
        ProgressPercent = 0;
        ProgressText = "正在准备…";
        _cts = new CancellationTokenSource();

        try
        {
            var job = await action(_cts.Token);
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
