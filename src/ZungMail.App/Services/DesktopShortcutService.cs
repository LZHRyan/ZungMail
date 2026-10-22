using System.IO;
using System.Text;

namespace ZungMail.App.Services;

/// <summary>
/// 在「当前用户桌面」创建 ZungMail 快捷方式。
/// <para>
/// 安装包（MSI）的快捷方式是写 <c>%PUBLIC%\Desktop</c> 的：有些机器上这个目录被清理工具删掉、
/// 或被安全软件拦写，安装时会弹「警告 1909 无法创建快捷方式」。这里给用户一个兜底入口——
/// 用当前用户桌面（普通权限即可写），不依赖安装器，也不需要重新安装。
/// </para>
/// </summary>
public static class DesktopShortcutService
{
    /// <summary>快捷方式文件名。</summary>
    public const string LinkFileName = "ZungMail.lnk";

    /// <summary>当前用户桌面上 ZungMail.lnk 的完整路径。</summary>
    public static string DefaultDesktopLinkPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LinkFileName);

    /// <summary>
    /// 创建（或覆盖）桌面快捷方式。失败不抛异常，返回原因供界面提示。
    /// </summary>
    public static bool TryCreate(out string linkPath, out string? error)
    {
        linkPath = DefaultDesktopLinkPath();
        error = null;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            error = "找不到当前程序的路径，无法创建快捷方式。";
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // WScript.Shell 是 Windows 自带的 COM，比 P/Invoke IShellLink 简单，且支持中文路径
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                error = "系统缺少 WScript.Shell 组件，无法创建快捷方式。";
                return false;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = exePath;
            link.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
            link.IconLocation = exePath + ",0";
            link.Description = "ZungMail 邮件客户端";
            link.Save();

            return File.Exists(linkPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>快捷方式是否已经存在（用于界面上的状态提示）。</summary>
    public static bool Exists() => File.Exists(DefaultDesktopLinkPath());
}
