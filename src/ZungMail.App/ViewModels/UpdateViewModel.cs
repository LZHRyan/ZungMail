using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Update.Protocol;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 升级窗口：展示版本与更新说明，负责下载、取消、安装与跳转设置。
/// 所有状态都来自 <see cref="UpdateCoordinator"/>，窗口关闭后不再保留订阅。
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly UpdateCoordinator _coordinator;
    private readonly UpdateInstallService _install;
    private readonly IDialogService _dialogs;
    private readonly IUpdateService _updateService;
    private readonly IApplicationVersion _applicationVersion;

    public UpdateViewModel(
        UpdateCoordinator coordinator,
        UpdateInstallService install,
        IDialogService dialogs,
        IUpdateService updateService,
        IApplicationVersion applicationVersion)
    {
        _coordinator = coordinator;
        _install = install;
        _dialogs = dialogs;
        _updateService = updateService;
        _applicationVersion = applicationVersion;

        _coordinator.StateChanged += OnCoordinatorStateChanged;
        RefreshFromCoordinator();

        // 打开窗口时如果本次启动还没检测过，自动检测一次，避免空白页
        if (_coordinator.Phase == UpdatePhase.Idle)
        {
            _ = CheckAsync();
        }
    }

    public event EventHandler? CloseRequested;

    [ObservableProperty] private string _statusText = "尚未检测新版本";
    [ObservableProperty] private string _localVersionText = "";
    [ObservableProperty] private string _addressText = "";
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _metaText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _mandatory;
    [ObservableProperty] private bool _hasRelease;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isReadyToInstall;
    [ObservableProperty] private bool _isPortablePackage;
    [ObservableProperty] private bool _canDownload;
    [ObservableProperty] private double _downloadPercent;

    public bool CanCheck => !IsChecking && !IsDownloading;

    public bool ShowProgress => IsDownloading;

    /// <summary>便携版 zip 不能自动安装：主按钮换成「打开所在文件夹」，不再显示「立即安装」。</summary>
    public bool ShowInstallButton => IsReadyToInstall && !IsPortablePackage;

    /// <summary>安装程序已就绪：保留左侧「打开所在文件夹」次要入口。</summary>
    public bool ShowOpenFolderSecondary => IsReadyToInstall && !IsPortablePackage;

    /// <summary>便携版已下载：主按钮直接打开文件夹，引导用户解压覆盖。</summary>
    public bool ShowOpenFolderPrimary => IsReadyToInstall && IsPortablePackage;

    /// <summary>检测到便携版时提前说明，避免下载完 70MB 才知道不能自动安装。</summary>
    public bool ShowPortableHint => IsPortablePackage && HasRelease;

    public string PortableHintText =>
        "便携版 zip 里是完整程序文件，不是安装程序：退出 ZungMail 后解压覆盖程序目录（默认 C:\\Program Files\\ZungMail），" +
        "或解压到任意目录直接运行 ZungMail.exe。本地邮件库、账户与设置都在 %APPDATA%\\ZungMail，覆盖程序文件不会影响它们。";

    public string DownloadButtonText => IsDownloading
        ? "下载中…"
        : IsPortablePackage
            ? "下载并打开文件夹"
            : "下载安装包";

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsChecking || IsDownloading) return;

        HasError = false;
        ErrorText = "";
        try
        {
            var result = await _coordinator.CheckAsync();
            if (result.Status is UpdateCheckStatus.NotConfigured or UpdateCheckStatus.InvalidAddress
                or UpdateCheckStatus.Unreachable or UpdateCheckStatus.ServerError)
            {
                HasError = true;
                ErrorText = result.Message;
            }
        }
        catch (OperationCanceledException)
        {
            // 窗口在检测途中被关闭
        }
        finally
        {
            RefreshFromCoordinator();
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (!CanDownload) return;

        HasError = false;
        ErrorText = "";
        var result = await _coordinator.DownloadAsync();
        if (!result.Success)
        {
            HasError = true;
            ErrorText = result.Message;
        }

        RefreshFromCoordinator();

        // 便携版没法「安装」：下载完直接把目录打开，用户解压即可（本机已有同一份文件时也打开，方便复用）
        if (result.Success && _coordinator.IsPortablePackage && _coordinator.InstallerPath is { Length: > 0 } path)
        {
            _install.RevealInExplorer(path);
        }
    }

    [RelayCommand]
    private void CancelDownload() => _coordinator.CancelDownload();

    [RelayCommand]
    private async Task InstallAsync()
    {
        var release = _coordinator.Release;
        var path = _coordinator.InstallerPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _coordinator.MarkInstallerMissing();
            HasError = true;
            ErrorText = "安装包已被移动或清理，请重新下载。";
            RefreshFromCoordinator();
            return;
        }

        // 便携版不走安装：直接打开所在文件夹，避免误点后还去启动一个 zip
        if (UpdatePackage.IsPortable(path))
        {
            _install.RevealInExplorer(path);
            return;
        }

        var launched = await _install.LaunchAsync(release, path);
        if (!launched && !File.Exists(path))
        {
            _coordinator.MarkInstallerMissing();
            RefreshFromCoordinator();
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_coordinator.InstallerPath is { Length: > 0 } path) _install.RevealInExplorer(path);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _dialogs.ShowSettings("update");
    }

    [RelayCommand]
    private void Later() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCoordinatorStateChanged(object? sender, UpdateStateChangedEventArgs e) => RefreshFromCoordinator();

    private void RefreshFromCoordinator()
    {
        var phase = _coordinator.Phase;
        var release = _coordinator.Release;

        LocalVersionText = "当前版本 v" + _applicationVersion.Version;
        AddressText = DescribeAddress();

        IsChecking = phase == UpdatePhase.Checking;
        IsDownloading = phase == UpdatePhase.Downloading;
        IsReadyToInstall = _coordinator.IsReadyToInstall;
        IsPortablePackage = _coordinator.IsPortablePackage;
        DownloadPercent = _coordinator.DownloadPercent;
        StatusText = _coordinator.StatusMessage;
        HasRelease = release is not null;
        Mandatory = release?.Mandatory == true;
        CanDownload = release is not null
                      && phase is UpdatePhase.UpdateAvailable or UpdatePhase.DownloadFailed;

        if (release is not null)
        {
            VersionText = "v" + release.Version;
            Notes = string.IsNullOrWhiteSpace(release.Notes) ? "这个版本没有提供更新说明。" : release.Notes;
            var size = release.Size > 0 ? FormatSize(release.Size) : "未知大小";
            // 包类型在检测阶段就写进概要，和下载后的按钮一致
            var package = UpdatePackage.Describe(
                release.FileName.Length > 0 ? release.FileName : release.DownloadUrl);
            var published = release.PublishedAt == default
                ? ""
                : $" · 发布于 {release.PublishedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            MetaText = $"{size} · {package} · 渠道 {release.Channel}{published}";
        }
        else
        {
            VersionText = "";
            Notes = "";
            MetaText = "";
        }

        // 检测/下载失败时把原因直接放进窗口，而不是只留在状态栏
        if (phase is UpdatePhase.NotConfigured or UpdatePhase.InvalidAddress
            or UpdatePhase.Unreachable or UpdatePhase.ServerError or UpdatePhase.DownloadFailed)
        {
            HasError = true;
            ErrorText = _coordinator.StatusMessage;
        }
        else if (!IsDownloading)
        {
            HasError = false;
            ErrorText = "";
        }

        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(DownloadButtonText));
        OnPropertyChanged(nameof(ShowInstallButton));
        OnPropertyChanged(nameof(ShowOpenFolderSecondary));
        OnPropertyChanged(nameof(ShowOpenFolderPrimary));
        OnPropertyChanged(nameof(ShowPortableHint));
        OnPropertyChanged(nameof(PortableHintText));
    }

    private string DescribeAddress()
    {
        var resolution = _updateService.ResolveAddress();
        if (!resolution.Success || resolution.Address is null)
        {
            return resolution.Source == UpdateAddressSource.Missing
                ? "升级服务地址：未配置"
                : "升级服务地址：不可用";
        }

        var source = resolution.Source == UpdateAddressSource.InstallFile ? "安装包配置" : "用户设置";
        return $"升级服务地址：{UpdateAddress.ToDisplay(resolution.Address)}（{source}）";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0} KB";
        return $"{kb / 1024:0.0} MB";
    }

    public void Dispose()
        => _coordinator.StateChanged -= OnCoordinatorStateChanged;
}
