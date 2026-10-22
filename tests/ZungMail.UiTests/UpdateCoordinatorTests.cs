using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ZungMail.App.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Update.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>升级状态机：未配置、不可达、发现新版本、下载失败与取消、重复检测复用。</summary>
public sealed class UpdateCoordinatorTests
{
    [Fact]
    public async Task Check_NotConfigured_KeepsBadgeHidden()
    {
        var service = new StubUpdateService
        {
            CheckResult = UpdateCheckResult.Fail(UpdateCheckStatus.NotConfigured, "未配置升级服务地址")
        };
        using var coordinator = Create(service);

        var result = await coordinator.CheckAsync();

        Assert.Equal(UpdateCheckStatus.NotConfigured, result.Status);
        Assert.Equal(UpdatePhase.NotConfigured, coordinator.Phase);
        Assert.False(coordinator.HasUpdate);
        Assert.Null(coordinator.Release);
        Assert.NotNull(coordinator.LastCheckAt);
    }

    [Fact]
    public async Task Check_Unreachable_SurfacesMessage()
    {
        var service = new StubUpdateService
        {
            CheckResult = UpdateCheckResult.Fail(UpdateCheckStatus.Unreachable, "无法连接升级服务：连接被拒绝")
        };
        using var coordinator = Create(service);

        await coordinator.CheckAsync();

        Assert.Equal(UpdatePhase.Unreachable, coordinator.Phase);
        Assert.Contains("连接被拒绝", coordinator.StatusMessage);
        Assert.False(coordinator.HasUpdate);
    }

    [Fact]
    public async Task Check_UpdateAvailable_ExposesReleaseAndBadgeState()
    {
        var release = BuildRelease("1.1.0");
        var service = new StubUpdateService
        {
            CheckResult = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable, "发现新版本 v1.1.0", release, null, DateTimeOffset.Now)
        };
        using var coordinator = Create(service);

        await coordinator.CheckAsync();

        Assert.Equal(UpdatePhase.UpdateAvailable, coordinator.Phase);
        Assert.True(coordinator.HasUpdate);
        Assert.Equal("1.1.0", coordinator.Release!.Version);
    }

    [Fact]
    public async Task Check_ConcurrentCalls_ReuseSameRequest()
    {
        var service = new StubUpdateService();
        var pending = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PendingCheck = pending;
        using var coordinator = Create(service);

        var first = coordinator.CheckAsync();
        var second = coordinator.CheckAsync();

        Assert.Same(first, second);
        Assert.Equal(1, service.CheckCalls);

        pending.SetResult(new UpdateCheckResult(
            UpdateCheckStatus.UpToDate, "已是最新版本", null, null, DateTimeOffset.Now));
        await first;
        await second;

        Assert.Equal(UpdatePhase.UpToDate, coordinator.Phase);
    }

    [Fact]
    public async Task Check_AfterUpdateDisappears_ClearsOldRelease()
    {
        var service = new StubUpdateService
        {
            CheckResult = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable, "发现新版本 v1.1.0", BuildRelease("1.1.0"), null, DateTimeOffset.Now)
        };
        using var coordinator = Create(service);
        await coordinator.CheckAsync();
        Assert.True(coordinator.HasUpdate);

        service.CheckResult = new UpdateCheckResult(
            UpdateCheckStatus.UpToDate, "已是最新版本", null, null, DateTimeOffset.Now);
        await coordinator.CheckAsync();

        Assert.False(coordinator.HasUpdate);
        Assert.Null(coordinator.Release);
        Assert.Equal(UpdatePhase.UpToDate, coordinator.Phase);
    }

    [Fact]
    public async Task Download_Success_MovesToReadyToInstall()
    {
        var installer = CreateTempInstaller("1.1.0");
        try
        {
            var service = new StubUpdateService
            {
                CheckResult = new UpdateCheckResult(
                    UpdateCheckStatus.UpdateAvailable, "发现新版本 v1.1.0", BuildRelease("1.1.0"), null, DateTimeOffset.Now),
                DownloadResult = UpdateDownloadResult.Ok(installer, reused: false)
            };
            using var coordinator = Create(service);
            await coordinator.CheckAsync();

            var result = await coordinator.DownloadAsync();

            Assert.True(result.Success);
            Assert.Equal(UpdatePhase.ReadyToInstall, coordinator.Phase);
            Assert.True(coordinator.IsReadyToInstall);
            Assert.Equal(installer, coordinator.InstallerPath);
        }
        finally
        {
            TryDelete(installer);
        }
    }

    [Fact]
    public async Task Download_Failure_AllowsRetry()
    {
        var service = new StubUpdateService
        {
            CheckResult = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable, "发现新版本 v1.1.0", BuildRelease("1.1.0"), null, DateTimeOffset.Now),
            DownloadResult = UpdateDownloadResult.Fail("安装包校验失败（SHA256 不一致）")
        };
        using var coordinator = Create(service);
        await coordinator.CheckAsync();

        await coordinator.DownloadAsync();

        Assert.Equal(UpdatePhase.DownloadFailed, coordinator.Phase);
        Assert.True(coordinator.HasUpdate);
        Assert.Contains("校验失败", coordinator.StatusMessage);
        Assert.False(coordinator.IsReadyToInstall);
    }

    [Fact]
    public async Task Download_Cancel_ReturnsToUpdateAvailable()
    {
        var service = new StubUpdateService
        {
            CheckResult = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable, "发现新版本 v1.1.0", BuildRelease("1.1.0"), null, DateTimeOffset.Now)
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DownloadHandler = async ct =>
        {
            started.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return UpdateDownloadResult.Ok("never.exe", false);
        };

        using var coordinator = Create(service);
        await coordinator.CheckAsync();

        var download = coordinator.DownloadAsync();
        await started.Task;
        coordinator.CancelDownload();
        var result = await download;

        Assert.False(result.Success);
        Assert.Equal(UpdatePhase.UpdateAvailable, coordinator.Phase);
        Assert.Contains("取消", result.Message);
    }

    [Fact]
    public async Task Download_WithoutRelease_Fails()
    {
        using var coordinator = Create(new StubUpdateService());

        var result = await coordinator.DownloadAsync();

        Assert.False(result.Success);
        Assert.Contains("还没有检测到", result.Message);
    }

    [Fact]
    public async Task IsReadyToInstall_FalseAfterFileRemoved()
    {
        var installer = CreateTempInstaller("1.1.0");
        var service = new StubUpdateService
        {
            CheckResult = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable, "发现新版本 v1.1.0", BuildRelease("1.1.0"), null, DateTimeOffset.Now)
        };
        using var coordinator = Create(service);
        await coordinator.CheckAsync();
        service.DownloadResult = UpdateDownloadResult.Ok(installer, false);
        await coordinator.DownloadAsync();

        Assert.True(coordinator.IsReadyToInstall);
        File.Delete(installer);
        Assert.False(coordinator.IsReadyToInstall);
    }

    [Fact]
    public async Task Download_LateProgressReport_DoesNotOverwriteReadyState()
    {
        var installer = CreateTempInstaller("1.1.0");
        try
        {
            var service = new StubUpdateService
            {
                CheckResult = new UpdateCheckResult(
                    UpdateCheckStatus.UpdateAvailable, "发现新版本 v1.1.0", BuildRelease("1.1.0"), null, DateTimeOffset.Now),
                DownloadResult = UpdateDownloadResult.Ok(installer, reused: false)
            };
            using var coordinator = Create(service);
            await coordinator.CheckAsync();

            // 假服务先报 100%，再返回成功；进度回调是异步投递的，迟到的回调不能把状态改回「下载中」
            await coordinator.DownloadAsync();
            await Task.Delay(150);

            Assert.Equal(UpdatePhase.ReadyToInstall, coordinator.Phase);
            Assert.Contains("下载完成", coordinator.StatusMessage);
            Assert.True(coordinator.IsReadyToInstall);
        }
        finally
        {
            TryDelete(installer);
        }
    }

    [Fact]
    public async Task PackageKind_FromReleaseFileName_BeforeDownload()
    {
        var msi = BuildRelease("1.1.0", "ZungMail-1.1.0-x64.msi");
        using var installerCoordinator = Create(new StubUpdateService
        {
            CheckResult = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable, "发现新版本 v1.1.0", msi, null, DateTimeOffset.Now)
        });
        await installerCoordinator.CheckAsync();
        Assert.Equal(UpdatePackageKind.Installer, installerCoordinator.PackageKind);
        Assert.False(installerCoordinator.IsPortablePackage);

        var zip = BuildRelease("1.1.0", "ZungMail-1.1.0-win-x64-selfcontained-portable.zip");
        using var portableCoordinator = Create(new StubUpdateService
        {
            CheckResult = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable, "发现新版本 v1.1.0", zip, null, DateTimeOffset.Now)
        });
        await portableCoordinator.CheckAsync();
        Assert.Equal(UpdatePackageKind.Portable, portableCoordinator.PackageKind);
        Assert.True(portableCoordinator.IsPortablePackage);
    }

    [Fact]
    public async Task Download_Portable_StatusSaysExtractInsteadOfInstall()
    {
        var portable = CreateTempPackage("1.1.0", ".zip");
        try
        {
            var service = new StubUpdateService
            {
                CheckResult = new UpdateCheckResult(
                    UpdateCheckStatus.UpdateAvailable,
                    "发现新版本 v1.1.0",
                    BuildRelease("1.1.0", "ZungMail-1.1.0-win-x64-selfcontained-portable.zip"),
                    null,
                    DateTimeOffset.Now),
                DownloadResult = UpdateDownloadResult.Ok(portable, reused: false)
            };
            using var coordinator = Create(service);
            await coordinator.CheckAsync();

            await coordinator.DownloadAsync();

            Assert.Equal(UpdatePhase.ReadyToInstall, coordinator.Phase);
            Assert.True(coordinator.IsReadyToInstall);
            // 「已就绪」但对 zip 不能提示「可以安装」：必须是「解压覆盖」
            Assert.Contains("便携版", coordinator.StatusMessage);
            Assert.Contains("解压", coordinator.StatusMessage);
            Assert.DoesNotContain("可以安装", coordinator.StatusMessage);
        }
        finally
        {
            TryDelete(portable);
        }
    }

    private static UpdateCoordinator Create(IUpdateService service)
        => new(service, new StubSettingsStore(), NullLogger<UpdateCoordinator>.Instance);

    private static UpdateReleaseInfo BuildRelease(string version, string? fileName = null) => new()
    {
        Id = "stable-" + version,
        Version = version,
        Channel = "stable",
        Notes = "测试说明",
        FileName = fileName ?? $"ZungMail-{version}-win-x64-setup.exe",
        Size = 1024,
        Sha256 = new string('c', 64),
        PublishedAt = DateTimeOffset.Now,
        DownloadUrl = "/api/update/download/stable-" + version
    };

    private static string CreateTempInstaller(string version) => CreateTempPackage(version, ".exe");

    private static string CreateTempPackage(string version, string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mc-update-coordinator-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"ZungMail-{version}-setup-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x00, 0x01]);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 测试产物清理失败不影响结论
        }
    }

    private sealed class StubSettingsStore : ISettingsStore
    {
        public AppSettings Current { get; } = new();

        public string? LoadWarning => null;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubUpdateService : IUpdateService
    {
        public UpdateCheckResult CheckResult { get; set; } =
            UpdateCheckResult.Fail(UpdateCheckStatus.UpToDate, "已是最新版本");

        public UpdateDownloadResult DownloadResult { get; set; } =
            UpdateDownloadResult.Fail("未配置下载结果");

        public TaskCompletionSource<UpdateCheckResult>? PendingCheck { get; set; }

        public Func<CancellationToken, Task<UpdateDownloadResult>>? DownloadHandler { get; set; }

        public int CheckCalls { get; private set; }

        public string DownloadsDirectory => Path.GetTempPath();

        public UpdateAddressResolution ResolveAddress()
            => UpdateAddressResolution.Missing("未配置");

        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
        {
            CheckCalls++;
            return PendingCheck?.Task ?? Task.FromResult(CheckResult);
        }

        public Task<UpdateDownloadResult> DownloadAsync(
            UpdateReleaseInfo release,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            progress?.Report(new UpdateDownloadProgress(release.Size, release.Size));
            return DownloadHandler?.Invoke(ct) ?? Task.FromResult(DownloadResult);
        }
    }
}
