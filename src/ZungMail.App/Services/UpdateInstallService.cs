using System.Diagnostics;
using System.IO;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Update.Protocol;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 安装引导：确认 → 保存草稿与设置 → 启动安装程序 → 关闭客户端。
/// 安装包只替换程序文件，数据目录（数据库、设置、模板、.eml）由安装器刻意不动。
/// </summary>
public sealed class UpdateInstallService
{
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<UpdateInstallService> _logger;

    public UpdateInstallService(
        ISettingsStore settings,
        IDialogService dialogs,
        ILogger<UpdateInstallService> logger)
    {
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>启动安装程序并在成功后退出客户端；返回 false 表示用户取消或启动失败。</summary>
    public async Task<bool> LaunchAsync(UpdateReleaseInfo? release, string? installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            _dialogs.Alert(
                "安装包不存在",
                "下载的安装包已被移动或清理，请重新下载后再安装。",
                DialogIcon.Warning,
                detail: installerPath);
            return false;
        }

        // 便携版 zip 只在这里兜底（界面已把「立即安装」换成「打开所在文件夹」）：不关客户端、不启动安装
        if (UpdatePackage.IsPortable(installerPath))
        {
            _dialogs.Alert(
                "便携版需要手动解压",
                "当前下载的是便携版 zip，不是安装程序。请退出 ZungMail 后解压覆盖程序目录，" +
                "或解压到任意目录直接运行 ZungMail.exe。",
                DialogIcon.Info,
                detail: installerPath);
            RevealInExplorer(installerPath);
            return false;
        }

        if (!UpdatePackage.IsInstaller(installerPath))
        {
            _dialogs.Alert(
                "无法安装",
                "升级服务提供的文件不是可执行的安装包（仅支持 .exe / .msi）。",
                DialogIcon.Warning,
                detail: installerPath);
            return false;
        }

        var version = string.IsNullOrWhiteSpace(release?.Version) ? "新版本" : "v" + release.Version;
        var confirmed = _dialogs.Confirm(
            $"安装 ZungMail {version}",
            "安装程序启动后 ZungMail 会自动关闭，未发送的草稿会先保存；装好后会自动重新打开。\r\n" +
            "本地邮件、账户、设置、模板与标签都不会被删除。",
            primaryText: "关闭客户端并安装",
            detail: installerPath,
            icon: DialogIcon.Question);
        if (!confirmed) return false;

        await FlushDraftsAsync().ConfigureAwait(true);

        try
        {
            await _settings.SaveAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 设置写不进去不阻断升级：下次启动仍会读旧值
            _logger.LogWarning(ex, "升级前保存设置失败");
        }

        try
        {
            var startInfo = new ProcessStartInfo(installerPath) { UseShellExecute = true };

            // .exe 安装包（NSIS）走静默安装：装完由安装器自动拉起客户端，真正做到「点一下 → 装好 → 自动回来」。
            // .msi 不加 /qn：MSI 自身只显示进度界面，装完由 LaunchZungMail 自定义动作拉起客户端。
            if (UpdatePackage.GetExtension(installerPath) == ".exe")
            {
                startInfo.Arguments = "/S";
            }

            Process.Start(startInfo);
            _logger.LogInformation("已启动升级安装程序：{Path}", installerPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动升级安装程序失败：{Path}", installerPath);
            _dialogs.Alert(
                "无法启动安装程序",
                "Windows 拒绝运行安装包，可能是权限或安全策略限制。可以手动到下载目录运行。",
                DialogIcon.Error,
                detail: ex.Message);
            return false;
        }

        // 安装器自己也会 taskkill 兜底，但先优雅退出能保证草稿与设置都已落盘
        System.Windows.Application.Current.Shutdown();
        return true;
    }

    /// <summary>打开安装包所在目录并选中文件；失败时退化为打开目录。</summary>
    public void RevealInExplorer(string installerPath)
    {
        try
        {
            if (File.Exists(installerPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{installerPath}\"") { UseShellExecute = true });
                return;
            }

            var directory = Path.GetDirectoryName(installerPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开安装包目录失败：{Path}", installerPath);
        }
    }

    /// <summary>关闭窗口前保存所有写邮件草稿，避免升级打断用户输入。</summary>
    private static async Task FlushDraftsAsync()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        foreach (var window in app.Windows.OfType<ComposeWindow>().ToList())
        {
            try
            {
                await window.PrepareForAppShutdownAsync();
            }
            catch (Exception)
            {
                // 单个窗口保存失败不阻断整体升级
            }
        }
    }
}
