using System.IO;
using ZungMail.Core.Abstractions;
using ZungMail.Update.Protocol;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>升级链路的状态，界面只读这一个枚举，避免多处布尔量互相打架。</summary>
public enum UpdatePhase
{
    /// <summary>本次启动还没有检测过。</summary>
    Idle,
    Checking,
    NotConfigured,
    InvalidAddress,
    UpToDate,
    UpdateAvailable,
    Downloading,
    ReadyToInstall,
    Unreachable,
    ServerError,
    DownloadFailed
}

public sealed class UpdateStateChangedEventArgs(UpdatePhase phase, string message) : EventArgs
{
    public UpdatePhase Phase { get; } = phase;

    public string Message { get; } = message;
}

/// <summary>
/// 升级协作者：把「检测 → 发现新版本 → 下载 → 待安装」收敛成一个状态机，
/// 供状态栏、设置页、托盘与启动后自动检测共用；本身不创建窗口。
/// 状态变更统一切回 UI 线程（构造时捕获的 SynchronizationContext）。
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    private readonly IUpdateService _service;
    private readonly ISettingsStore _settings;
    private readonly ILogger<UpdateCoordinator> _logger;
    private readonly SynchronizationContext? _uiContext;

    private readonly object _gate = new();
    private Task<UpdateCheckResult>? _checkTask;
    private CancellationTokenSource? _downloadCts;
    private int _downloadGeneration;
    private double _downloadReportedPercent = -1;

    private UpdatePhase _phase = UpdatePhase.Idle;
    private string _statusMessage = "尚未检测新版本";
    private UpdateReleaseInfo? _release;
    private string? _installerPath;
    private double _downloadPercent;
    private DateTimeOffset? _lastCheckAt;

    public UpdateCoordinator(
        IUpdateService service,
        ISettingsStore settings,
        ILogger<UpdateCoordinator> logger)
    {
        _service = service;
        _settings = settings;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;
    }

    public UpdatePhase Phase
    {
        get { lock (_gate) return _phase; }
    }

    public string StatusMessage
    {
        get { lock (_gate) return _statusMessage; }
    }

    public UpdateReleaseInfo? Release
    {
        get { lock (_gate) return _release; }
    }

    public string? InstallerPath
    {
        get { lock (_gate) return _installerPath; }
    }

    public double DownloadPercent
    {
        get { lock (_gate) return _downloadPercent; }
    }

    public DateTimeOffset? LastCheckAt
    {
        get { lock (_gate) return _lastCheckAt; }
    }

    public bool IsChecking => Phase == UpdatePhase.Checking;

    public bool IsDownloading => Phase == UpdatePhase.Downloading;

    /// <summary>存在可用版本（检测到、下载中、待安装或下载失败后重试）。</summary>
    public bool HasUpdate => Phase is UpdatePhase.UpdateAvailable
        or UpdatePhase.Downloading
        or UpdatePhase.ReadyToInstall
        or UpdatePhase.DownloadFailed;

    public bool IsReadyToInstall => Phase == UpdatePhase.ReadyToInstall
                                    && InstallerPath is { Length: > 0 } path
                                    && File.Exists(path);

    /// <summary>
    /// 本次发布/已下载的包是什么类型：已下载时以落盘文件为准，否则先按服务端给的文件名判断。
    /// 便携版 zip 不能启动安装，界面必须换成「打开所在文件夹」。
    /// </summary>
    public UpdatePackageKind PackageKind
    {
        get
        {
            lock (_gate)
            {
                var kind = UpdatePackage.Classify(_installerPath);
                if (kind != UpdatePackageKind.Unknown) return kind;

                kind = UpdatePackage.Classify(_release?.FileName);
                return kind != UpdatePackageKind.Unknown
                    ? kind
                    : UpdatePackage.Classify(_release?.DownloadUrl);
            }
        }
    }

    /// <summary>是否为便携版 zip（需手动解压覆盖，不可自动安装）。</summary>
    public bool IsPortablePackage => PackageKind == UpdatePackageKind.Portable;

    public event EventHandler<UpdateStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 检测新版本。检测进行中再次调用会复用同一次请求，不会并发打服务端。
    /// 失败不抛异常，结果对象里带可展示的原因。
    /// </summary>
    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_checkTask is { IsCompleted: false }) return _checkTask;
            _checkTask = CheckCoreAsync(ct);
            return _checkTask;
        }
    }

    /// <summary>下载当前可用版本；取消时返回失败结果并保留「发现新版本」状态。</summary>
    public async Task<UpdateDownloadResult> DownloadAsync()
    {
        UpdateReleaseInfo? release;
        CancellationTokenSource cts;
        int generation;
        lock (_gate)
        {
            if (_phase == UpdatePhase.Downloading) return UpdateDownloadResult.Fail("安装包正在下载中。");
            release = _release;
            if (release is null) return UpdateDownloadResult.Fail("还没有检测到可用的新版本。");

            _downloadCts = new CancellationTokenSource();
            cts = _downloadCts;
            _downloadPercent = 0;
            _downloadReportedPercent = -1;
            generation = ++_downloadGeneration;
        }

        Publish(UpdatePhase.Downloading, $"正在下载 v{release.Version}…");

        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            var text = p.IsIndeterminate
                ? $"正在下载 v{release.Version}（{FormatSize(p.BytesReceived)}）"
                : $"正在下载 v{release.Version}（{p.Percent:0}%，{FormatSize(p.BytesReceived)} / {FormatSize(p.TotalBytes ?? 0)}）";

            PublishDownloadProgress(generation, text, p.Percent, p.IsIndeterminate);
        });

        try
        {
            var result = await _service.DownloadAsync(release, progress, cts.Token).ConfigureAwait(false);
            if (result.Success && result.FilePath is { Length: > 0 } path)
            {
                lock (_gate) _installerPath = path;

                // 便携版 zip 不能自动安装：状态文案直接说清「解压覆盖」，别让用户等一个不存在的安装步骤
                Publish(UpdatePhase.ReadyToInstall, UpdatePackage.IsPortable(path)
                    ? result.Reused
                        ? $"便携版 v{release.Version} 已在本机，解压后覆盖程序目录即可"
                        : $"便携版 v{release.Version} 下载完成，解压后覆盖程序目录即可"
                    : result.Reused
                        ? $"v{release.Version} 已在本机，可直接安装"
                        : $"v{release.Version} 下载完成，可以安装");
            }
            else
            {
                Publish(UpdatePhase.DownloadFailed, result.Message);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("用户取消了安装包下载");
            Publish(UpdatePhase.UpdateAvailable, "下载已取消，可随时重新下载");
            return UpdateDownloadResult.Fail("下载已取消。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载安装包异常");
            Publish(UpdatePhase.DownloadFailed, "下载失败：" + ex.Message);
            return UpdateDownloadResult.Fail("下载失败：" + ex.Message);
        }
        finally
        {
            // 让下载期间排队但尚未执行的进度回调全部失效
            Interlocked.Increment(ref _downloadGeneration);
            lock (_gate)
            {
                if (ReferenceEquals(_downloadCts, cts)) _downloadCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>取消正在进行的下载（没有下载时无副作用）。</summary>
    public void CancelDownload()
    {
        lock (_gate)
        {
            _downloadCts?.Cancel();
        }
    }

    /// <summary>安装包被外部清理时把状态拉回「可重新下载」。</summary>
    public void MarkInstallerMissing()
    {
        lock (_gate) _installerPath = null;
        Publish(UpdatePhase.UpdateAvailable, "安装包已被清理，请重新下载");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken ct)
    {
        Publish(UpdatePhase.Checking, "正在检测新版本…");

        UpdateCheckResult result;
        try
        {
            result = await _service.CheckAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Publish(UpdatePhase.Idle, "检测已取消");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "升级检测异常");
            result = UpdateCheckResult.Fail(UpdateCheckStatus.ServerError, "升级检测失败：" + ex.Message);
        }

        lock (_gate)
        {
            _lastCheckAt = DateTimeOffset.Now;
        }

        var phase = result.Status switch
        {
            UpdateCheckStatus.NotConfigured => UpdatePhase.NotConfigured,
            UpdateCheckStatus.InvalidAddress => UpdatePhase.InvalidAddress,
            UpdateCheckStatus.UpToDate => UpdatePhase.UpToDate,
            UpdateCheckStatus.UpdateAvailable => UpdatePhase.UpdateAvailable,
            UpdateCheckStatus.Unreachable => UpdatePhase.Unreachable,
            _ => UpdatePhase.ServerError
        };

        if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Release is not null)
        {
            lock (_gate) _release = result.Release;
        }
        else if (result.Status != UpdateCheckStatus.UpdateAvailable)
        {
            // 服务端已没有更高版本（或被下线）时清掉旧标识，避免一直显示过期提示
            lock (_gate) _release = null;
        }

        Publish(phase, result.Message);
        return result;
    }

    private void Publish(UpdatePhase phase, string message)
    {
        void Apply()
        {
            lock (_gate)
            {
                _phase = phase;
                _statusMessage = message;
            }

            // 事件在锁外触发：订阅方（界面/托盘）会读属性，避免在持锁状态下回调
            StateChanged?.Invoke(this, new UpdateStateChangedEventArgs(phase, message));
        }

        Dispatch(Apply);
    }

    /// <summary>
    /// 下载进度单独发布：在真正落到界面线程时再校验「这一轮下载是否还在进行」，
    /// 避免尾随的 100% 回调把「待安装 / 失败」状态覆盖回「下载中」。
    /// </summary>
    private void PublishDownloadProgress(int generation, string message, double percent, bool indeterminate)
    {
        void Apply()
        {
            lock (_gate)
            {
                if (generation != _downloadGeneration || _phase != UpdatePhase.Downloading) return;
                if (!indeterminate && percent <= _downloadReportedPercent) return;

                if (!indeterminate) _downloadReportedPercent = percent;
                _downloadPercent = percent;
                _statusMessage = message;
            }

            StateChanged?.Invoke(this, new UpdateStateChangedEventArgs(UpdatePhase.Downloading, message));
        }

        Dispatch(Apply);
    }

    private void Dispatch(Action action)
    {
        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return bytes + " B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0} KB";
        return $"{kb / 1024:0.0} MB";
    }
}
