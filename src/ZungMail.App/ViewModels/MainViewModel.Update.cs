using CommunityToolkit.Mvvm.Input;
using ZungMail.App.Services;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 主窗口里的升级入口：状态栏「新版本」标识与打开升级窗口的命令。
/// 单独放一个文件，避免继续把 10 万字符的 MainViewModel 撑大。
/// </summary>
public sealed partial class MainViewModel
{
    private UpdateCoordinator? _updateCoordinator;
    private UpdateWindowService? _updateWindowService;

    /// <summary>由构造函数调用：订阅升级状态，状态栏据此显示/隐藏版本标识。</summary>
    private void InitializeUpdate(UpdateCoordinator coordinator, UpdateWindowService windowService)
    {
        _updateCoordinator = coordinator;
        _updateWindowService = windowService;
        _updateCoordinator.StateChanged += (_, _) => RaiseUpdateBadgeChanged();
    }

    /// <summary>是否有可用升级（发现新版本、下载中、待安装或下载失败可重试）。</summary>
    public bool HasUpdateBadge => _updateCoordinator?.HasUpdate == true;

    /// <summary>状态栏标识文本：区分「新版本 / 下载中 / 待安装 / 重试」。</summary>
    public string UpdateBadgeText
    {
        get
        {
            var coordinator = _updateCoordinator;
            var release = coordinator?.Release;
            if (coordinator is null || release is null) return "检查更新";

            return coordinator.Phase switch
            {
                UpdatePhase.Downloading => $"下载中 {coordinator.DownloadPercent:0}%",
                UpdatePhase.ReadyToInstall => $"待安装 v{release.Version}",
                UpdatePhase.DownloadFailed => $"重试升级 v{release.Version}",
                _ => release.Mandatory ? $"必须更新 v{release.Version}" : $"新版本 v{release.Version}"
            };
        }
    }

    public string UpdateBadgeToolTip
    {
        get
        {
            var coordinator = _updateCoordinator;
            if (coordinator is null) return "检查新版本";
            var hint = coordinator.IsReadyToInstall
                ? "安装包已下载，点击查看并安装"
                : "点击查看版本说明与下载";
            return $"{coordinator.StatusMessage}\n{hint}";
        }
    }

    /// <summary>状态栏标识、托盘菜单与设置页共用的入口：打开升级窗口。</summary>
    [RelayCommand]
    private void ShowUpdateWindow() => _updateWindowService?.Show();

    private void RaiseUpdateBadgeChanged()
    {
        OnPropertyChanged(nameof(HasUpdateBadge));
        OnPropertyChanged(nameof(UpdateBadgeText));
        OnPropertyChanged(nameof(UpdateBadgeToolTip));
    }
}
