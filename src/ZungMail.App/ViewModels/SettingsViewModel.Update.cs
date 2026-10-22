using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using ZungMail.Update.Protocol;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>设置中心的「升级」分区：服务地址、渠道、自动检测与手动检测。</summary>
public sealed partial class SettingsViewModel : IDisposable
{
    private static readonly int[] UpdateDelayMinutes = { 1, 3, 5, 10, 30 };

    private static readonly string[] UpdateChannelValues = { "stable", "beta" };

    private static readonly string[] UpdateChannelLabels = { "stable（正式版）", "beta（测试版）" };

    private UpdateCoordinator? _updateCoordinator;
    private UpdateWindowService? _updateWindowService;
    private IUpdateService? _updateService;

    private void InitializeUpdate(
        UpdateCoordinator coordinator,
        UpdateWindowService windowService,
        IUpdateService updateService)
    {
        _updateCoordinator = coordinator;
        _updateWindowService = windowService;
        _updateService = updateService;
        _updateCoordinator.StateChanged += OnUpdateStateChanged;
    }

    /// <summary>升级分区是否可见（供 XAML 绑定）。</summary>
    public bool IsUpdateSection => SelectedSection.Key == "update";

    public bool UpdateAutoCheckEnabled
    {
        get => Settings.UpdateAutoCheckEnabled;
        set
        {
            if (value == Settings.UpdateAutoCheckEnabled) return;
            Settings.UpdateAutoCheckEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateStatusText));
        }
    }

    public string UpdateServiceUrl
    {
        get => Settings.UpdateServiceUrl;
        set
        {
            if (value == Settings.UpdateServiceUrl) return;
            Settings.UpdateServiceUrl = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateAddressPreview));
            OnPropertyChanged(nameof(UpdateAddressIsError));
        }
    }

    public IReadOnlyList<string> UpdateChannelOptions { get; } = UpdateChannelLabels;

    public IReadOnlyList<string> UpdateDelayOptions { get; } = new[]
    {
        "启动后 1 分钟", "启动后 3 分钟", "启动后 5 分钟", "启动后 10 分钟", "启动后 30 分钟"
    };

    public int UpdateChannelIndex
    {
        get
        {
            var index = Array.IndexOf(UpdateChannelValues, (Settings.UpdateChannel ?? "").Trim().ToLowerInvariant());
            return index < 0 ? 0 : index;
        }
        set
        {
            if (value < 0 || value >= UpdateChannelValues.Length) return;
            if (Settings.UpdateChannel == UpdateChannelValues[value]) return;
            Settings.UpdateChannel = UpdateChannelValues[value];
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateAddressPreview));
        }
    }

    public int UpdateDelayIndex
    {
        get => SettingsTier.NearestIndex(UpdateDelayMinutes, AppSettings.EffectiveUpdateDelayMinutes(Settings.UpdateAutoCheckDelayMinutes));
        set
        {
            if (value < 0 || value >= UpdateDelayMinutes.Length) return;
            if (Settings.UpdateAutoCheckDelayMinutes == UpdateDelayMinutes[value]) return;
            Settings.UpdateAutoCheckDelayMinutes = UpdateDelayMinutes[value];
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateStatusText));
        }
    }

    /// <summary>地址来源与生效地址的说明：用户设置优先，其次安装目录 update.json。</summary>
    public string UpdateAddressPreview
    {
        get
        {
            var typed = (Settings.UpdateServiceUrl ?? "").Trim();
            if (typed.Length > 0)
            {
                return UpdateAddress.TryNormalize(typed, out var address, out var error)
                    ? "生效地址：" + UpdateAddress.ToDisplay(address!)
                    : "地址不合法：" + error;
            }

            if (_updateService is null) return "";

            var resolution = _updateService.ResolveAddress();
            if (resolution.Success && resolution.Address is not null)
            {
                var source = resolution.Source == UpdateAddressSource.InstallFile ? "安装包 update.json" : "用户设置";
                return $"来自{source}：{UpdateAddress.ToDisplay(resolution.Address)}";
            }

            return resolution.Source == UpdateAddressSource.Missing
                ? "尚未配置升级服务地址。"
                : resolution.Message;
        }
    }

    public bool UpdateAddressIsError
    {
        get
        {
            var typed = (Settings.UpdateServiceUrl ?? "").Trim();
            if (typed.Length > 0)
            {
                return !UpdateAddress.TryNormalize(typed, out _, out _);
            }

            // 安装包配置了但写错也提示；完全未配置只是普通状态，不标红
            if (_updateService?.ResolveAddress() is { Success: false, Source: UpdateAddressSource.InstallFile }) return true;
            return false;
        }
    }

    /// <summary>设置页当前状态：未配置 / 检测中 / 已是最新 / 发现新版本 / 连接失败。</summary>
    public string UpdateStatusText
    {
        get
        {
            var coordinator = _updateCoordinator;
            if (coordinator is null) return "";

            var text = coordinator.Phase switch
            {
                UpdatePhase.Idle => Settings.UpdateAutoCheckEnabled
                    ? $"尚未检测，启动后 {AppSettings.EffectiveUpdateDelayMinutes(Settings.UpdateAutoCheckDelayMinutes)} 分钟自动检测"
                    : "尚未检测（自动检测已关闭）",
                UpdatePhase.Checking => "正在检测新版本…",
                UpdatePhase.NotConfigured => "未配置升级服务地址，自动检测不会发起请求",
                UpdatePhase.InvalidAddress => "升级服务地址不合法：" + coordinator.StatusMessage,
                UpdatePhase.UpToDate => "已是最新版本",
                UpdatePhase.UpdateAvailable => coordinator.StatusMessage,
                UpdatePhase.Downloading => coordinator.StatusMessage,
                UpdatePhase.ReadyToInstall => coordinator.StatusMessage,
                UpdatePhase.Unreachable => "无法连接升级服务：" + coordinator.StatusMessage,
                UpdatePhase.ServerError => "升级服务响应异常：" + coordinator.StatusMessage,
                UpdatePhase.DownloadFailed => "下载失败：" + coordinator.StatusMessage,
                _ => coordinator.StatusMessage
            };

            if (coordinator.LastCheckAt is { } last)
            {
                text += $"\n上次检测：{last.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            }

            return text;
        }
    }

    /// <summary>有可用版本时设置页显示「查看升级」，否则只显示「立即检测」。</summary>
    public bool HasAvailableUpdate => _updateCoordinator?.HasUpdate == true;

    public string UpdateActionText => _updateCoordinator?.IsReadyToInstall == true
        ? "安装新版本"
        : _updateCoordinator?.HasUpdate == true
            ? "查看升级"
            : "立即检测";

    [RelayCommand]
    private async Task CheckUpdatesNowAsync()
    {
        if (_updateCoordinator is null) return;

        StatusText = "正在检测新版本…";
        try
        {
            var result = await _updateCoordinator.CheckAsync();
            if (result.Status is UpdateCheckStatus.Unreachable or UpdateCheckStatus.ServerError)
            {
                _dialogs.Alert("检查更新失败", result.Message, DialogIcon.Warning);
            }
            else if (result.Status is UpdateCheckStatus.NotConfigured or UpdateCheckStatus.InvalidAddress)
            {
                _dialogs.Alert(
                    "还没有可用的升级服务地址",
                    result.Message,
                    DialogIcon.Info,
                    detail: "填写地址后点「保存设置」，再点「立即检测」即可验证。");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "手动检查更新失败");
            _dialogs.Alert("检查更新失败", ex.Message, DialogIcon.Error);
        }
        finally
        {
            StatusText = "就绪";
            OnPropertyChanged(nameof(UpdateStatusText));
            OnPropertyChanged(nameof(HasAvailableUpdate));
            OnPropertyChanged(nameof(UpdateActionText));
        }
    }

    /// <summary>「查看升级 / 安装新版本」：打开升级窗口，下载与安装都在那里完成。</summary>
    [RelayCommand]
    private void OpenUpdateWindow() => _updateWindowService?.Show();

    private void OnUpdateStateChanged(object? sender, UpdateStateChangedEventArgs e)
    {
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(HasAvailableUpdate));
        OnPropertyChanged(nameof(UpdateActionText));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_updateCoordinator is not null)
        {
            _updateCoordinator.StateChanged -= OnUpdateStateChanged;
        }

        // 关窗时还可能有排队的模板预览：取消并释放，否则它会把整个视图模型拖到任务结束
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
    }
}
