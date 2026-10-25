using System.Security.AccessControl;
using System.Security.Principal;
using ZungMail.Core.Abstractions;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// 应用路径约定：根目录默认在用户漫游数据目录下的 ZungMail 文件夹，
/// 也允许用户改到其它本地目录（位置记在固定位置的引导指针里，见 <see cref="DataDirectoryPointer"/>）。
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths()
    {
        // 首次启动时指针不存在 → 使用默认目录；
        // 有待迁移时仍落在源目录（详见 DataDirectoryPointer.ResolveEffective），
        // 否则启动瞬间的日志会写进目标目录，把下一次迁移挡在「目录不是空的」外面。
        RootDirectory = DataDirectoryPointer.ResolveEffective(new DataDirectoryPointer().Read());
    }

    public AppPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    public string RootDirectory { get; }

    public string DatabasePath => Path.Combine(RootDirectory, "mail.db");

    public string TemplatesPath => Path.Combine(RootDirectory, "templates.json");

    public string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    public string EmlDirectory => Path.Combine(RootDirectory, "eml");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(EmlDirectory);
        RestrictToCurrentUser(RootDirectory);
    }

    /// <summary>把数据目录 ACL 收紧为仅当前用户，降低同机其它账户旁路读取凭据与数据库的风险。</summary>
    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows()) return;

        var user = WindowsIdentity.GetCurrent().User;
        if (user is null) return;

        try
        {
            var directory = new DirectoryInfo(path);

            // 已经收紧过就直接返回：重写 ACL 会让 Windows 把继承的 ACE
            // 重新传播到整棵子树，而数据目录下有上万个 eml 文件 / 上千个子目录，
            // 慢盘或装了杀毒软件的机器上每次启动要几秒到几十秒 —— 这一步只需做一次。
            if (IsAlreadyRestricted(directory, user)) return;

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        catch (Exception)
        {
            // 非 NTFS 卷或权限不足时无法收紧，属可接受降级，不阻断启动。
        }
    }

    /// <summary>
    /// 判断目录是否已是「继承已断开 + 当前用户完全控制」。
    /// 只读安全描述符，不写 ACL，所以不会触发子树的继承传播。
    /// </summary>
    private static bool IsAlreadyRestricted(DirectoryInfo directory, SecurityIdentifier user)
    {
        // 平台守卫放在方法内部：调用方虽然已经判过 IsWindows，但分析器看不懂跨方法守卫（CA1416）
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            if (!directory.Exists) return false;

            var current = directory.GetAccessControl();
            if (!current.AreAccessRulesProtected) return false;

            foreach (var entry in current.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (entry is not FileSystemAccessRule rule) continue;
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (!rule.IdentityReference.Equals(user)) continue;
                if ((rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl) continue;

                return true;
            }

            return false;
        }
        catch (Exception)
        {
            // 读不到 ACL（权限受限）：按「未收紧」处理，走正常收紧流程
            return false;
        }
    }
}
