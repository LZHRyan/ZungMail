using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 「AI 总结」卡片的状态机：待生成 / 生成中（可取消）/ 结果 / 失败。
/// 两个阅读窗口各持有一个实例（主窗口右栏与独立阅读窗口互不影响）。
/// </summary>
public sealed partial class MailSummaryViewModel : ObservableObject
{
    private readonly IMailSummaryService _service;
    private readonly ILogger<MailSummaryViewModel> _logger;

    private CancellationTokenSource? _cts;
    private EmailDetail? _detail;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private string _providerLabel = "";

    [ObservableProperty]
    private string _elapsedText = "";

    /// <summary>降级 / 截断等提示（琥珀色一行）。</summary>
    [ObservableProperty]
    private string _noticeText = "";

    public bool HasNotice => NoticeText.Length > 0;

    [ObservableProperty]
    private IReadOnlyList<string> _bullets = Array.Empty<string>();

    /// <summary>要点段的展示文本（只读 TextBox 绑定，支持鼠标选中局部复制）。</summary>
    [ObservableProperty]
    private string _bulletsText = "";

    [ObservableProperty]
    private IReadOnlyList<string> _actions = Array.Empty<string>();

    [ObservableProperty]
    private string _actionsText = "";

    [ObservableProperty]
    private IReadOnlyList<string> _references = Array.Empty<string>();

    [ObservableProperty]
    private string _referencesText = "";

    private string _copyText = "";

    public MailSummaryViewModel(
        IMailSummaryService service,
        ILogger<MailSummaryViewModel> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>有邮件就能总结：卡片只在用户点了工具栏「AI 总结」后才显示。</summary>
    public bool CanSummarize => _detail is not null;

    partial void OnNoticeTextChanged(string value) => OnPropertyChanged(nameof(HasNotice));

    /// <summary>切换邮件时清空卡片并取消上一次推理。</summary>
    public void Reset()
    {
        Cancel();
        _detail = null;
        IsVisible = false;
        IsLoading = false;
        HasResult = false;
        HasError = false;
        StatusText = "";
        ErrorText = "";
        ProviderLabel = "";
        ElapsedText = "";
        NoticeText = "";
        Bullets = Array.Empty<string>();
        Actions = Array.Empty<string>();
        References = Array.Empty<string>();
        BulletsText = "";
        ActionsText = "";
        ReferencesText = "";
        _copyText = "";
    }

    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已完成并释放
        }
    }

    /// <summary>按钮入口：显示卡片并生成（force = 重新生成）。</summary>
    [RelayCommand]
    private Task SummarizeAsync() => ShowAsync(_detail, force: false);

    /// <summary>卡片上的「重新生成」：忽略缓存。</summary>
    [RelayCommand]
    private Task RegenerateAsync() => ShowAsync(_detail, force: true);

    [RelayCommand]
    private void Collapse() => IsVisible = false;

    /// <summary>用于「复制摘要」；由窗口代码后置写剪贴板，视图模型不碰 WPF。</summary>
    public string BuildCopyText() => _copyText;

    public async Task ShowAsync(EmailDetail? detail, bool force = false)
    {
        if (detail is null) return;

        _detail = detail;

        Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        IsVisible = true;
        IsLoading = true;
        HasError = false;
        HasResult = false;
        ErrorText = "";
        NoticeText = "";
        ProviderLabel = "";
        ElapsedText = "";
        StatusText = "正在准备摘要…";
        Bullets = Array.Empty<string>();
        Actions = Array.Empty<string>();
        References = Array.Empty<string>();
        BulletsText = "";
        ActionsText = "";
        ReferencesText = "";

        var progress = new Progress<MailSummaryProgress>(update =>
        {
            if (!ReferenceEquals(_cts, cts)) return;
            StatusText = string.IsNullOrWhiteSpace(update.Detail)
                ? update.Stage
                : update.Stage + " · " + update.Detail;
        });

        try
        {
            var result = await _service.SummarizeAsync(detail.Id, force, progress, cts.Token).ConfigureAwait(true);
            if (!ReferenceEquals(_cts, cts)) return;

            ApplyResult(result);
            StatusText = "";
        }
        catch (OperationCanceledException)
        {
            // 切换邮件或收起卡片：保持安静
        }
        catch (MailSummaryUnavailableException ex)
        {
            if (!ReferenceEquals(_cts, cts)) return;
            HasError = true;
            ErrorText = ex.Message;
            StatusText = "";
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_cts, cts)) return;
            _logger.LogWarning(ex, "生成邮件摘要失败：邮件 {Id}", detail.Id);
            HasError = true;
            ErrorText = "生成摘要失败：" + ex.Message;
            StatusText = "";
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

    private void ApplyResult(MailSummaryResult result)
    {
        HasResult = true;
        ProviderLabel = result.ProviderName;
        ElapsedText = result.ElapsedMs >= 1000
            ? $"{result.ElapsedMs / 1000.0:F1}s"
            : $"{result.ElapsedMs}ms";

        Bullets = ToDisplayList(result.Bullets);
        Actions = ToDisplayList(result.Actions);
        References = ToDisplayList(result.References);
        BulletsText = ToDisplayText(Bullets);
        ActionsText = ToDisplayText(Actions);
        ReferencesText = ToDisplayText(References);

        var notices = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.FallbackReason)) notices.Add(result.FallbackReason);
        if (result.ChunkCount > 1)
        {
            notices.Add($"正文较长，已分 {result.ChunkCount} 段总结（覆盖 {result.InputChars} 字）");
        }

        if (result.Truncated)
        {
            notices.Add("超出覆盖范围的内容没有总结，可在 设置 → 本地 AI 助手 调大「覆盖上限」");
        }
        NoticeText = string.Join("；", notices);

        _copyText = result.ToPlainText();
        OnPropertyChanged(nameof(CanSummarize));
    }

    /// <summary>空段落显示「未提及」，与规格一致。</summary>
    private static IReadOnlyList<string> ToDisplayList(IReadOnlyList<string> items)
        => items.Count == 0 ? new[] { "未提及" } : items;

    /// <summary>把一段的多条内容拼成可选中复制的多行文本（每条一行，前缀「· 」）。</summary>
    private static string ToDisplayText(IReadOnlyList<string> items)
        => string.Join(Environment.NewLine, ToDisplayList(items).Select(item => "· " + item));
}
