using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 「查看邮件源码」窗口：负责取源码、展示来源/编码/体积文案与失败状态。
/// 剪贴板与保存对话框放在窗口代码后置，视图模型不碰 WPF 对话框，便于测试。
/// </summary>
public sealed partial class MailSourceViewModel : ObservableObject
{
    private readonly IMailSourceService _source;
    private readonly ILogger<MailSourceViewModel> _logger;

    /// <summary>本次加载的取消源：关窗或重新获取时取消上一次请求。</summary>
    private CancellationTokenSource? _cts;

    private long _messageId;

    [ObservableProperty]
    private string _subject = "";

    /// <summary>来源徽标文案：服务器原始源码 / 本地重建源码。</summary>
    [ObservableProperty]
    private string _sourceLabel = "";

    /// <summary>回退到本地重建的原因；为空表示拿到了服务器原文。</summary>
    [ObservableProperty]
    private string _fallbackReason = "";

    /// <summary>编码 · 体积 · 生成时间。</summary>
    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorText = "";

    /// <summary>当前展示的源码；另存为 .eml 时写它的完整字节。</summary>
    public MailSourceSnapshot? Snapshot { get; private set; }

    public bool HasFallback => FallbackReason.Length > 0;

    public bool HasSource => Snapshot is not null;

    /// <summary>保存对话框的默认文件名：主题.eml。</summary>
    public string SuggestedFileName => MailSourceFileWriter.SuggestFileName(Subject);

    public MailSourceViewModel(IMailSourceService source, ILogger<MailSourceViewModel> logger)
    {
        _source = source;
        _logger = logger;
    }

    partial void OnFallbackReasonChanged(string value) => OnPropertyChanged(nameof(HasFallback));

    /// <summary>打开（或切换到）一封邮件并加载源码。</summary>
    public async Task OpenAsync(long messageId, string subject, bool preferServerSource = true)
    {
        _messageId = messageId;
        Subject = string.IsNullOrWhiteSpace(subject) ? "（无主题）" : subject;
        OnPropertyChanged(nameof(SuggestedFileName));

        await LoadAsync(preferServerSource).ConfigureAwait(true);
    }

    /// <summary>重新获取：一律先试服务器原文（回退后用户点了「重新获取」就是这个入口）。</summary>
    [RelayCommand]
    private Task ReloadAsync() => LoadAsync(preferServerSource: true);

    /// <summary>取消尚未完成的请求（窗口关闭时调用）。</summary>
    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经完成并释放：忽略
        }
    }

    private async Task LoadAsync(bool preferServerSource)
    {
        Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        IsLoading = true;
        HasError = false;
        ErrorText = "";
        FallbackReason = "";
        Snapshot = null;
        Text = "";
        SourceLabel = "";
        SummaryText = "";
        StatusText = "正在获取源码…";

        try
        {
            var snapshot = await _source.LoadAsync(_messageId, preferServerSource, cts.Token).ConfigureAwait(true);
            if (!ReferenceEquals(_cts, cts)) return;   // 期间又切了邮件/重新获取：丢弃旧结果

            Snapshot = snapshot;
            Text = snapshot.Text;
            SourceLabel = snapshot.KindLabel;
            FallbackReason = snapshot.FallbackReason ?? "";
            SummaryText = $"{snapshot.EncodingName} · {DisplayFormat.FileSize(snapshot.SizeBytes)} · " +
                          $"{snapshot.GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            StatusText = snapshot.IsTruncated
                ? "源码超过 4 MB，已截断显示；另存 .eml 可获取完整源码"
                : $"共 {snapshot.Bytes.Length / 1024.0:F1} KB，可全选复制或另存为 .eml";
            OnPropertyChanged(nameof(HasSource));
        }
        catch (OperationCanceledException)
        {
            // 关窗或被下一次请求取代：保持安静
        }
        catch (MailSourceUnavailableException ex)
        {
            if (!ReferenceEquals(_cts, cts)) return;
            HasError = true;
            ErrorText = ex.Message;
            StatusText = "获取源码失败";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_cts, cts)) return;
            _logger.LogWarning(ex, "查看邮件源码失败：邮件 {Id}", _messageId);
            HasError = true;
            ErrorText = "获取源码失败：" + ex.Message;
            StatusText = "获取源码失败";
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsLoading = false;
                _cts = null;
            }

            cts.Dispose();
        }
    }
}
