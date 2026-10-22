using System.IO;
using Microsoft.Win32;

namespace ZungMail.App.Services;

/// <summary>管理当前 Windows 用户的登录后自启动项（HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。</summary>
public interface IStartupLaunchService
{
    bool IsEnabled();
    void SetEnabled(bool enabled);

    /// <summary>把旧品牌（MailClient）遗留的自启动值迁移到当前值名；没有旧值时不做事。</summary>
    void MigrateLegacyValue();
}

public sealed class StartupLaunchService : IStartupLaunchService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZungMail";

    /// <summary>改名前的自启动值名。升级后它指向已被移除的 MailClient.exe，不清掉会让登录时弹找不到文件。</summary>
    private const string LegacyValueName = "MailClient";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 启动项注册表。") ;

        // 不管开还是关，旧值都不再有意义：它指向已经被卸载掉的 MailClient.exe
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("无法确定当前客户端的可执行文件路径。");
        }

        // Run 项以命令行方式执行；始终加引号以兼容包含空格的安装目录。
        key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
    }

    /// <summary>
    /// 旧版本开着「开机自启」的用户，升级后应继续自启：把旧值改写成本次运行的程序路径。
    /// 旧值已存在但新值不存在时才动，避免覆盖用户后来在设置里做的选择。
    /// </summary>
    public void MigrateLegacyValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(LegacyValueName) is not string legacy || string.IsNullOrWhiteSpace(legacy)) return;
            if (key.GetValue(ValueName) is string current && !string.IsNullOrWhiteSpace(current))
            {
                // 新值已就位：只清掉旧值
                using var writable = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                writable?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                return;
            }

            SetEnabled(true);
        }
        catch (Exception)
        {
            // 注册表被策略锁定时不影响启动：自启失效比启动失败好
        }
    }
}
