using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 设置 →「本地 AI 助手」：开关、引擎选择、摘要长度、正文上限，以及模型状态与下载/删除。
/// <para>
/// 引擎是可插拔的，这里只列「已知引擎 + 自动」；模型管理通过 <see cref="ISummaryModelStore"/> 抽象完成，
/// 不依赖任何具体推理实现。
/// </para>
/// </summary>
public sealed partial class SettingsViewModel
{
    // 这两个字段由主构造函数（SettingsViewModel.cs）赋值
    private readonly ISummaryModelStore _summaryModels;
    private readonly IMailSummaryService _summaryService;

    // ---------------- 引擎与长度 ----------------

    public IReadOnlyList<string> MailSummaryProviderOptions { get; } = new[]
    {
        "自动（本地模型优先，缺模型时用规则抽取）",
        "本地模型（Qwen2.5-0.5B，约 400 MB）",
        "规则抽取（零依赖，秒出结果）"
    };

    public int MailSummaryProviderIndex
    {
        get => MailSummarySettings.NormalizeProvider(Settings.MailSummaryProvider) switch
        {
            MailSummarySettings.LocalModelProviderId => 1,
            MailSummarySettings.RuleBasedProviderId => 2,
            _ => 0
        };
        set
        {
            Settings.MailSummaryProvider = Math.Clamp(value, 0, 2) switch
            {
                1 => MailSummarySettings.LocalModelProviderId,
                2 => MailSummarySettings.RuleBasedProviderId,
                _ => MailSummarySettings.AutoProviderId
            };
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> MailSummaryLengthOptions { get; } = new[] { "短（3 条要点）", "标准", "详细" };

    public int MailSummaryLengthIndex
    {
        get => MailSummarySettings.ResolveLength(Settings.MailSummaryLength) switch
        {
            MailSummaryLength.Short => 0,
            MailSummaryLength.Detailed => 2,
            _ => 1
        };
        set
        {
            Settings.MailSummaryLength = MailSummarySettings.LengthToSetting(Math.Clamp(value, 0, 2) switch
            {
                0 => MailSummaryLength.Short,
                2 => MailSummaryLength.Detailed,
                _ => MailSummaryLength.Normal
            });
            OnPropertyChanged();
        }
    }

    // ---------------- 模型状态 ----------------

    [ObservableProperty]
    private string _mailSummaryModelStatusText = "正在检查模型状态…";

    [ObservableProperty]
    private string _mailSummaryModelDetail = "";

    [ObservableProperty]
    private bool _isMailSummaryModelBusy;

    [ObservableProperty]
    private double _mailSummaryModelPercent;

    public string MailSummaryModelDirectory => _summaryModels.ModelDirectory;

    /// <summary>刷新模型状态（进入设置页、下载/删除/重新检测之后调用）。</summary>
    public void RefreshMailSummaryModelStatus()
    {
        try
        {
            var status = _summaryModels.Recheck();
            ApplyModelStatus(status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取摘要模型状态失败");
            MailSummaryModelStatusText = "模型状态读取失败";
            MailSummaryModelDetail = ex.Message;
        }
    }

    private void ApplyModelStatus(SummaryModelStatus status)
    {
        MailSummaryModelStatusText = status.State switch
        {
            SummaryModelState.Ready => "模型已就绪（" + FormatSize(status.SizeBytes) + "）",
            SummaryModelState.Downloading => "正在下载模型…",
            SummaryModelState.Invalid => "模型不可用",
            _ => "尚未下载模型"
        };

        MailSummaryModelDetail = status.State switch
        {
            SummaryModelState.Ready => status.FilePath,
            SummaryModelState.Invalid => status.Detail ?? "校验失败，可删除后重新下载",
            _ => "本地小模型约 400 MB，按需下载到本机（不打进安装包）。也可以把 GGUF 文件放进目录后点「重新检测」。"
        };

        OnPropertyChanged(nameof(MailSummaryModelDirectory));
    }

    [RelayCommand]
    private async Task DownloadMailSummaryModelAsync()
    {
        if (IsMailSummaryModelBusy) return;

        IsMailSummaryModelBusy = true;
        MailSummaryModelPercent = 0;
        MailSummaryModelStatusText = "正在下载模型…";
        MailSummaryModelDetail = "下载完成后会用 SHA256 校验，校验不通过会自动删除。";

        var progress = new Progress<MailSummaryProgress>(update =>
        {
            MailSummaryModelStatusText = update.Stage;
            if (update.Fraction is { } fraction) MailSummaryModelPercent = fraction * 100;
            if (!string.IsNullOrWhiteSpace(update.Detail)) MailSummaryModelDetail = update.Detail;
        });

        try
        {
            var status = await _summaryModels.DownloadAsync(progress).ConfigureAwait(true);
            ApplyModelStatus(status);
            _summaryService.ResetProviders();
            StatusText = status.State == SummaryModelState.Ready ? "摘要模型已就绪" : "模型下载完成但校验未通过";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消模型下载";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "下载摘要模型失败");
            ApplyModelStatus(_summaryModels.GetStatus());
            MailSummaryModelDetail = "下载失败：" + ex.Message;
            StatusText = "下载摘要模型失败";
        }
        finally
        {
            IsMailSummaryModelBusy = false;
        }
    }

    [RelayCommand]
    private void DeleteMailSummaryModel()
    {
        try
        {
            _summaryModels.Delete();
            _summaryService.ResetProviders();
            ApplyModelStatus(_summaryModels.GetStatus());
            StatusText = "已删除摘要模型";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除摘要模型失败");
            StatusText = "删除模型失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void RecheckMailSummaryModel()
    {
        RefreshMailSummaryModelStatus();
        _summaryService.ResetProviders();
        StatusText = "已重新检测摘要模型";
    }

    [RelayCommand]
    private void OpenMailSummaryModelFolder()
    {
        try
        {
            Directory.CreateDirectory(_summaryModels.ModelDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_summaryModels.ModelDirectory}\"")
            {
                UseShellExecute = true
            });
            StatusText = "已打开模型目录";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开摘要模型目录失败");
            StatusText = "打开目录失败：" + ex.Message;
        }
    }

    private static string FormatSize(long bytes) => bytes <= 0
        ? "0 MB"
        : bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:F1} GB"
            : $"{bytes / 1024.0 / 1024:F0} MB";
}
