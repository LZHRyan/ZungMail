using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.Core.Abstractions;
using ZungMail.Update.Protocol;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 设置中心的「反馈」分区：分类 + 描述 + 可选联系方式，
/// 按需附带脱敏日志与系统环境，提交到升级服务；失败时保留输入并可复制内容离线发送。
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>描述少于这个字数就没法定位问题，直接禁止提交。</summary>
    public const int FeedbackDescriptionMinLength = 10;

    public const int FeedbackDescriptionMaxLength = 4000;

    private IFeedbackService? _feedbackService;
    private FeedbackRequest? _lastFeedbackRequest;
    private bool _feedbackPrepared;

    private void InitializeFeedback(IFeedbackService feedbackService) => _feedbackService = feedbackService;

    /// <summary>反馈分区是否可见（供 XAML 绑定）。</summary>
    public bool IsFeedbackSection => SelectedSection.Key == "feedback";

    public IReadOnlyList<string> FeedbackCategoryOptions { get; } =
        FeedbackCategories.All.Select(FeedbackCategories.Label).ToList();

    [ObservableProperty] private int _feedbackCategoryIndex;
    [ObservableProperty] private string _feedbackDescription = "";
    [ObservableProperty] private string _feedbackContact = "";
    [ObservableProperty] private bool _feedbackAttachLog = true;
    [ObservableProperty] private bool _feedbackAttachEnvironment = true;
    [ObservableProperty] private bool _isSubmittingFeedback;
    [ObservableProperty] private string _feedbackResultText = "";
    [ObservableProperty] private bool _feedbackResultIsError;
    [ObservableProperty] private string _feedbackSubmittedId = "";
    [ObservableProperty] private string _feedbackLogHint = "";

    public bool HasFeedbackResult => FeedbackResultText.Length > 0;

    public bool HasFeedbackSubmittedId => FeedbackSubmittedId.Length > 0;

    public bool CanSubmitFeedback =>
        !IsSubmittingFeedback && FeedbackDescription.Trim().Length >= FeedbackDescriptionMinLength;

    public string FeedbackSubmitText => IsSubmittingFeedback ? "提交中…" : "提交反馈";

    /// <summary>字数提示：既告诉最少字数，也提示已输入多少，避免用户反复试。</summary>
    public string FeedbackDescriptionHint
    {
        get
        {
            var length = FeedbackDescription.Trim().Length;
            if (length == 0) return $"请至少描述 {FeedbackDescriptionMinLength} 个字，写清「做了什么、看到什么、期望什么」";
            if (length < FeedbackDescriptionMinLength) return $"还差 {FeedbackDescriptionMinLength - length} 个字（当前 {length} 字）";
            if (length > FeedbackDescriptionMaxLength) return $"超出上限 {length - FeedbackDescriptionMaxLength} 字（最多 {FeedbackDescriptionMaxLength} 字）";
            return $"已输入 {length} 字";
        }
    }

    partial void OnFeedbackDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(CanSubmitFeedback));
        OnPropertyChanged(nameof(FeedbackDescriptionHint));
    }

    partial void OnIsSubmittingFeedbackChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSubmitFeedback));
        OnPropertyChanged(nameof(FeedbackSubmitText));
    }

    partial void OnFeedbackResultTextChanged(string value) => OnPropertyChanged(nameof(HasFeedbackResult));

    partial void OnFeedbackSubmittedIdChanged(string value) => OnPropertyChanged(nameof(HasFeedbackSubmittedId));

    /// <summary>进入分区时准备：默认带入当前账户邮箱、说明将附加哪个日志文件。</summary>
    private async Task PrepareFeedbackAsync()
    {
        if (!_feedbackPrepared)
        {
            _feedbackPrepared = true;
            FeedbackLogHint = DescribeLog();
        }

        if (!string.IsNullOrWhiteSpace(FeedbackContact)) return;

        try
        {
            var accounts = await _accounts.GetAllAsync().ConfigureAwait(true);
            var account = accounts.Where(a => a.IsEnabled).OrderBy(a => a.SortOrder).FirstOrDefault()
                          ?? accounts.FirstOrDefault();
            if (account is not null && string.IsNullOrWhiteSpace(FeedbackContact))
            {
                FeedbackContact = account.Email;
            }
        }
        catch (Exception ex)
        {
            // 读不到账户不影响提交，联系方式留空即可
            _logger.LogDebug(ex, "反馈：读取默认账户失败");
        }
    }

    private string DescribeLog()
    {
        if (_feedbackService is null) return "";
        var info = _feedbackService.InspectLog();
        return info.Available ? info.Description : "未找到可附加的日志：" + info.Description;
    }

    [RelayCommand]
    private async Task SubmitFeedbackAsync()
    {
        if (_feedbackService is null || IsSubmittingFeedback) return;

        var description = FeedbackDescription.Trim();
        if (description.Length < FeedbackDescriptionMinLength)
        {
            FeedbackResultIsError = true;
            FeedbackResultText = $"请至少描述 {FeedbackDescriptionMinLength} 个字，方便定位问题。";
            return;
        }

        if (description.Length > FeedbackDescriptionMaxLength)
        {
            FeedbackResultIsError = true;
            FeedbackResultText = $"描述最多 {FeedbackDescriptionMaxLength} 字，请精简后再提交。";
            return;
        }

        IsSubmittingFeedback = true;
        FeedbackResultText = "";
        FeedbackSubmittedId = "";
        StatusText = "正在提交反馈…";
        try
        {
            var request = await _feedbackService.BuildRequestAsync(BuildDraft()).ConfigureAwait(true);
            _lastFeedbackRequest = request;

            var result = await _feedbackService.SubmitAsync(request).ConfigureAwait(true);
            FeedbackResultIsError = !result.Success;
            if (result.Success)
            {
                FeedbackSubmittedId = result.FeedbackId ?? "";
                FeedbackResultText = result.Message + "，已填内容保留在此，确认可点「清空」。";
            }
            else
            {
                FeedbackResultText = result.Message + "；已填内容已保留，可点「复制反馈内容」另行发送。";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "提交反馈失败");
            FeedbackResultIsError = true;
            FeedbackResultText = "提交反馈失败：" + ex.Message;
        }
        finally
        {
            IsSubmittingFeedback = false;
            StatusText = "就绪";
        }
    }

    /// <summary>离线降级：把完整内容（含环境与日志）复制成纯文本，可直接粘贴给开发者。</summary>
    [RelayCommand]
    private async Task CopyFeedbackContentAsync()
    {
        try
        {
            var request = _lastFeedbackRequest;
            if (request is null && _feedbackService is not null)
            {
                request = await _feedbackService.BuildRequestAsync(BuildDraft()).ConfigureAwait(true);
                _lastFeedbackRequest = request;
            }

            if (request is null) return;

            Clipboard.SetText(FeedbackText.ToPlainText(request));
            FeedbackResultIsError = false;
            FeedbackResultText = "反馈内容已复制到剪贴板，可直接粘贴发送。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "复制反馈内容失败");
            FeedbackResultIsError = true;
            FeedbackResultText = "复制失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void CopyFeedbackId()
    {
        if (!HasFeedbackSubmittedId) return;

        try
        {
            Clipboard.SetText(FeedbackSubmittedId);
            StatusText = "已复制反馈编号：" + FeedbackSubmittedId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "复制反馈编号失败");
        }
    }

    [RelayCommand]
    private void ClearFeedback()
    {
        FeedbackDescription = "";
        FeedbackResultText = "";
        FeedbackResultIsError = false;
        FeedbackSubmittedId = "";
        _lastFeedbackRequest = null;
    }

    private FeedbackDraft BuildDraft()
    {
        var index = Math.Clamp(FeedbackCategoryIndex, 0, FeedbackCategories.All.Length - 1);
        return new FeedbackDraft(
            FeedbackCategories.All[index],
            FeedbackDescription.Trim(),
            FeedbackContact,
            FeedbackAttachLog,
            FeedbackAttachEnvironment);
    }
}
