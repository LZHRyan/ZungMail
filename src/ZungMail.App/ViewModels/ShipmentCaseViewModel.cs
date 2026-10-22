using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 「一票货」案件页：把同一票货的全部邮件、时效节点、附件聚在一页，
/// 支持导出资料包、关闭 / 重新打开案件，以及回到主窗口按单号筛选。
/// </summary>
public sealed partial class ShipmentCaseViewModel : ObservableObject
{
    private readonly IShipmentRepository _shipments;
    private readonly ShipmentPackageExporter _exporter;
    private readonly Action<string>? _applySearch;
    private readonly ILogger<ShipmentCaseViewModel> _logger;
    private readonly long _caseId;

    private ShipmentCaseDetail? _detail;

    public ShipmentCaseViewModel(
        IShipmentRepository shipments,
        ShipmentPackageExporter exporter,
        long caseId,
        Action<string>? applySearch,
        ILogger<ShipmentCaseViewModel> logger)
    {
        _shipments = shipments;
        _exporter = exporter;
        _caseId = caseId;
        _applySearch = applySearch;
        _logger = logger;
    }

    /// <summary>请求关闭窗口（参数为对话框结果）。</summary>
    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<CaseMessageRow> Messages { get; } = new();

    public ObservableCollection<DeadlineBoardItem> Deadlines { get; } = new();

    public ObservableCollection<CaseAttachmentRow> Attachments { get; } = new();

    [ObservableProperty]
    private string _caseRef = "";

    [ObservableProperty]
    private string _caseKindLabel = "";

    [ObservableProperty]
    private string _caseKindShort = "";

    [ObservableProperty]
    private string _caseKindColor = "#2563eb";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _messageCountDisplay = "";

    [ObservableProperty]
    private string _nextDeadlineDisplay = "";

    [ObservableProperty]
    private string _lastActivityDisplay = "";

    /// <summary>业务阶段（由邮件类型推进）。</summary>
    [ObservableProperty]
    private string _stageText = "";

    [ObservableProperty]
    private bool _hasStage;

    [ObservableProperty]
    private bool _hasException;

    [ObservableProperty]
    private string _exportStatus = "";

    [ObservableProperty]
    private bool _isClosed;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasMessages => Messages.Count > 0;

    public bool HasDeadlines => Deadlines.Count > 0;

    public bool HasAttachments => Attachments.Count > 0;

    public string ToggleStatusText => IsClosed ? "重新打开案件" : "关闭案件";

    /// <summary>在主窗口按该案件主键筛选（也用于「这票货还有哪些邮件」）。</summary>
    public string SearchText => string.IsNullOrWhiteSpace(CaseRef)
        ? ""
        : $"{FreightReference.SearchKeywordOf(_detail?.Summary.PrimaryKind ?? FreightReferenceKind.Booking)}:{CaseRef}";

    partial void OnIsClosedChanged(bool value) => OnPropertyChanged(nameof(ToggleStatusText));

    public async Task InitializeAsync()
    {
        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task ReloadAsync()
    {
        try
        {
            IsBusy = true;
            var detail = await _shipments.GetCaseAsync(_caseId).ConfigureAwait(true);
            if (detail is null)
            {
                ExportStatus = "该案件已不存在（相关邮件可能已被清理）";
                return;
            }

            _detail = detail;
            CaseRef = detail.Summary.PrimaryRef;
            CaseKindLabel = detail.Summary.PrimaryKindLabel;
            CaseKindShort = detail.Summary.PrimaryKindShort;
            CaseKindColor = detail.Summary.PrimaryKindColor;
            StatusText = detail.Summary.StatusText;
            IsClosed = detail.Summary.IsClosed;
            MessageCountDisplay = detail.Summary.MessageCountDisplay;
            NextDeadlineDisplay = detail.Summary.NextDeadlineDisplay;
            LastActivityDisplay = detail.Summary.LastActivityDisplay;
            StageText = detail.Summary.StageText;
            HasStage = detail.Summary.HasStage;
            HasException = detail.Summary.HasException;

            Messages.Clear();
            foreach (var message in detail.Messages) Messages.Add(message);
            Deadlines.Clear();
            foreach (var deadline in detail.Deadlines) Deadlines.Add(deadline);
            Attachments.Clear();
            foreach (var attachment in detail.Attachments) Attachments.Add(attachment);

            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(HasDeadlines));
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(SearchText));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取一票货案件失败：{Id}", _caseId);
            ExportStatus = "读取案件失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleStatusAsync()
    {
        try
        {
            var next = IsClosed ? ShipmentCaseStatus.Open : ShipmentCaseStatus.Closed;
            await _shipments.SetCaseStatusAsync(_caseId, next).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            ExportStatus = next == ShipmentCaseStatus.Closed
                ? "案件已关闭，不会再出现在时效看板"
                : "案件已重新打开";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新案件状态失败：{Id}", _caseId);
            ExportStatus = "更新案件状态失败：" + ex.Message;
        }
    }

    /// <summary>把整票货的邮件与附件打成一个 zip，用于交接、对账、客户要全套单据。</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_detail is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "导出资料包",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = SafeFileName(CaseRef) + "_资料包.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            IsBusy = true;
            var export = await _shipments.GetCaseExportAsync(_caseId).ConfigureAwait(true);
            if (export is null)
            {
                ExportStatus = "该案件已不存在，无法导出";
                return;
            }

            var result = await _exporter.ExportAsync(export, dialog.FileName).ConfigureAwait(true);
            ExportStatus = result.Summary + " → " + dialog.FileName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "导出资料包失败：{Id}", _caseId);
            ExportStatus = "导出失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>回到主窗口，按该票货的单号筛选全部往来邮件。</summary>
    [RelayCommand]
    private void FilterInMain()
    {
        if (!string.IsNullOrWhiteSpace(SearchText)) _applySearch?.Invoke(SearchText);
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void OpenAttachment(CaseAttachmentRow? attachment)
    {
        if (attachment is null || !attachment.IsAvailable) return;
        try
        {
            Process.Start(new ProcessStartInfo(attachment.LocalPath!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "打开附件失败：{Path}", attachment.LocalPath);
            ExportStatus = "无法打开附件：" + ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, true);

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch).ToArray();
        var text = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(text) ? "shipment" : text;
    }
}
