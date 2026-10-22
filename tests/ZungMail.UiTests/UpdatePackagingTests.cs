using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 升级体验回归（A+B 两条）：
/// A —— 便携版 zip 不能当安装包用，升级窗口必须换成「打开所在文件夹」并说明要手动解压；
/// B —— 两种安装包都要在装完后自动把客户端拉起来（静默安装尤其重要，客户端内点「立即安装」走的就是它）。
/// 这些都是打包脚本 / XAML 里的约定，用源码断言守住，避免以后被静默改回去。
/// </summary>
public sealed class UpdatePackagingTests
{
    private static string NsiPath => Path.Combine(AppContext.BaseDirectory, "Packaging", "ZungMail.nsi");

    private static string MsiSourcePath => Path.Combine(AppContext.BaseDirectory, "Packaging", "New-MsiSource.ps1");

    private static string BuildMsiPath => Path.Combine(AppContext.BaseDirectory, "Packaging", "build-msi.ps1");

    private static string BuildInstallerPath => Path.Combine(AppContext.BaseDirectory, "Packaging", "build-installer.ps1");

    private static string SignScriptPath => Path.Combine(AppContext.BaseDirectory, "Packaging", "sign.ps1");

    private static string BuildPropsPath => Path.Combine(AppContext.BaseDirectory, "Packaging", "Directory.Build.props");

    private static string UpdateWindowPath => Path.Combine(AppContext.BaseDirectory, "Views", "UpdateWindow.xaml");

    [Fact]
    public void InstallerScriptsAreAvailableToTests()
    {
        Assert.True(File.Exists(NsiPath), "缺少 NSIS 脚本副本：" + NsiPath);
        Assert.True(File.Exists(MsiSourcePath), "缺少 MSI 源生成脚本副本：" + MsiSourcePath);
        Assert.True(File.Exists(UpdateWindowPath), "缺少升级窗口 XAML 副本：" + UpdateWindowPath);
        Assert.True(File.Exists(BuildMsiPath), "缺少 MSI 构建脚本副本：" + BuildMsiPath);
        Assert.True(File.Exists(BuildInstallerPath), "缺少安装包构建脚本副本：" + BuildInstallerPath);
        Assert.True(File.Exists(SignScriptPath), "缺少签名脚本副本：" + SignScriptPath);
        Assert.True(File.Exists(BuildPropsPath), "缺少 Directory.Build.props 副本：" + BuildPropsPath);
    }

    [Fact]
    public void SilentInstallLaunchesClientAfterCopyingFiles()
    {
        var script = File.ReadAllText(NsiPath);
        var installStart = script.IndexOf("Section \"ZungMail 主程序\"", StringComparison.Ordinal);
        var desktopStart = script.IndexOf("Section \"桌面快捷方式\"", StringComparison.Ordinal);
        Assert.True(installStart > 0 && desktopStart > installStart, "找不到主安装段");

        var installSection = script[installStart..desktopStart];

        // 客户端内「立即安装」用 /S 启动安装包，装完必须自己把客户端打开
        Assert.Contains("${If} ${Silent}", installSection, StringComparison.Ordinal);
        Assert.Contains(@"Exec '""$WINDIR\explorer.exe"" ""$INSTDIR\${APP_EXE}""'", installSection, StringComparison.Ordinal);

        // 用 explorer 转发启动：安装器是管理员权限，直接 Exec 会让客户端以管理员身份运行（拖放会被 UIPI 拦掉）
        Assert.DoesNotContain(@"Exec '""$INSTDIR\${APP_EXE}""'", installSection, StringComparison.Ordinal);

        // 文件没写进去时不能假装成功：静默安装要报错退出
        Assert.Contains("SetErrorLevel 1", installSection, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveInstallStillOffersRunOnFinishPage()
    {
        var script = File.ReadAllText(NsiPath);
        Assert.Contains("MUI_FINISHPAGE_RUN \"$INSTDIR\\${APP_EXE}\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MsiLaunchesClientOnlyForRealLocalInstalls()
    {
        var script = File.ReadAllText(MsiSourcePath);

        Assert.Contains("Id=\"LaunchZungMail\"", script, StringComparison.Ordinal);
        Assert.Contains("After=\"InstallFinalize\"", script, StringComparison.Ordinal);
        Assert.Contains("Property Id=\"LAUNCHAPP\" Value=\"1\"", script, StringComparison.Ordinal);

        // 卸载（REMOVE=ALL）与静默部署（UILevel=2，即 /qn）都不许启动客户端
        Assert.Contains("LAUNCHAPP = 1 AND NOT REMOVE~=&quot;ALL&quot; AND UILevel &gt; 2", script, StringComparison.Ordinal);

        // 同样用 explorer 转发，避免客户端继承管理员令牌
        Assert.Contains("[WindowsFolder]explorer.exe", script, StringComparison.Ordinal);
        Assert.Contains("Return=\"asyncNoWait\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MsiDoesNotCheckOsVersionWithVersionNt()
    {
        var script = File.ReadAllText(MsiSourcePath);

        // msiexec 的兼容模式会把 VersionNT / WindowsBuild 模拟成旧版本，
        // 「VersionNT >= 1000」在 Win10 上必然为假 —— 曾经就是它让安装直接失败（装完没有 ZungMail.exe）。
        // 现在只剩 64 位这一条启动条件。
        var launchConditions = script
            .Split('\n')
            .Where(line => line.Contains("<Launch Condition=", StringComparison.Ordinal))
            .ToList();

        var condition = Assert.Single(launchConditions);
        Assert.Contains("VersionNT64", condition, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionNT &gt;= 1000", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateWindowSwitchesPrimaryActionForPortablePackage()
    {
        var xaml = File.ReadAllText(UpdateWindowPath);

        // 便携版：提前说明要手动解压，主按钮从「立即安装」改成「打开所在文件夹」
        Assert.Contains("{Binding ShowPortableHint,", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding PortableHintText}", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding ShowInstallButton,", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding ShowOpenFolderPrimary,", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding ShowOpenFolderSecondary,", xaml, StringComparison.Ordinal);

        // 下载按钮文案要随包类型变化（便携版是「下载并打开文件夹」）
        Assert.Contains("Content=\"{Binding DownloadButtonText}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductVersionAndArtifactNamesComeFromDirectoryBuildProps()
    {
        var props = File.ReadAllText(BuildPropsPath);
        Assert.Contains("<Product>ZungMail</Product>", props, StringComparison.Ordinal);

        var match = Regex.Match(props, "<Version>\\s*(\\d+\\.\\d+\\.\\d+)\\s*</Version>");
        Assert.True(match.Success, "Directory.Build.props 里没有三段式 <Version>");
        var version = Version.Parse(match.Groups[1].Value);
        // 改名版本至少 1.2.0：已发布的 MailClient 1.1.x 不允许用同一个版本号重打
        Assert.True(
            version.CompareTo(new Version(1, 2, 0)) >= 0,
            "改名后的版本号应 >= 1.2.0，实际 " + version);

        // MSI：版本读 Directory.Build.props，产物名固定 ZungMail-<版本>-x64.msi
        var msi = File.ReadAllText(BuildMsiPath);
        Assert.Contains("Directory.Build.props", msi, StringComparison.Ordinal);
        Assert.Contains("\"ZungMail-$Version-x64.msi\"", msi, StringComparison.Ordinal);

        // NSIS：安装包与便携版都按 ZungMail-<版本>-<运行时>-<形态>-* 命名
        var setup = File.ReadAllText(BuildInstallerPath);
        Assert.Contains("Directory.Build.props", setup, StringComparison.Ordinal);
        Assert.Contains("\"ZungMail-$numericVersion-$Runtime-$flavor-setup.exe\"", setup, StringComparison.Ordinal);
        Assert.Contains("\"ZungMail-$numericVersion-$Runtime-$flavor-portable.zip\"", setup, StringComparison.Ordinal);

        // 产物名不能残留旧品牌
        Assert.DoesNotContain("MailClient-$", msi, StringComparison.Ordinal);
        Assert.DoesNotContain("MailClient-$", setup, StringComparison.Ordinal);

        // 两个脚本都要把 .msi 收进 SHA256SUMS.txt：
        // 否则先跑 build-msi 再跑 build-installer，MSI 的哈希会被挤出校验文件
        const string hashFilter = "$_.Extension -in '.exe', '.msi', '.zip'";
        Assert.Contains(hashFilter, msi, StringComparison.Ordinal);
        Assert.Contains(hashFilter, setup, StringComparison.Ordinal);
    }

    [Fact]
    public void MsiSourceKeepsUpgradeCodeAndZungMailInstallLayout()
    {
        var script = File.ReadAllText(MsiSourcePath);

        // 改名绝不能换 UpgradeCode：MailClient 1.1.x 的 MSI 靠它原地升级，换了就变成并排安装
        Assert.Contains(
            "[string]$UpgradeCode = '{D2A9F5D4-6D7B-4F7A-9C2E-71B5E3A8C6F1}'",
            script,
            StringComparison.Ordinal);
        Assert.Contains("MajorUpgrade AllowSameVersionUpgrades=\"yes\"", script, StringComparison.Ordinal);

        // 产品名 / 安装目录 / 开始菜单程序组 / 快捷方式 / 注册表键全部是 ZungMail
        Assert.Contains("[string]$ProductName = 'ZungMail'", script, StringComparison.Ordinal);
        Assert.Contains("Id=\"INSTALLFOLDER\" Name=\"ZungMail\"", script, StringComparison.Ordinal);
        Assert.Contains("Id=\"ProgramMenuDir\" Name=\"ZungMail\"", script, StringComparison.Ordinal);
        Assert.Contains("Shortcut Id=\"StartMenuShortcut\" Name=\"ZungMail\"", script, StringComparison.Ordinal);
        Assert.Contains("Shortcut Id=\"DesktopShortcut\" Name=\"ZungMail\"", script, StringComparison.Ordinal);
        Assert.Contains("Target=\"[INSTALLFOLDER]ZungMail.exe\"", script, StringComparison.Ordinal);
        Assert.Contains("Key=\"Software\\ZungMail\"", script, StringComparison.Ordinal);
        Assert.Contains("Icon Id=\"ZungMailIcon\" SourceFile=\"ZungMail.exe\"", script, StringComparison.Ordinal);

        // 装完自动拉起客户端（改名后的动作名与可执行文件名）
        Assert.Contains("Id=\"LaunchZungMail\"", script, StringComparison.Ordinal);
        Assert.Contains("&quot;[INSTALLFOLDER]ZungMail.exe&quot;", script, StringComparison.Ordinal);

        // 生成器里不能残留旧品牌，否则会写出 MailClient 的目录或 ARP 名称
        Assert.DoesNotContain("MailClient", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignScriptAcceptsBothEnvironmentVariablePrefixes()
    {
        var script = File.ReadAllText(SignScriptPath);

        // 新名 ZUNGMAIL_SIGN_* 优先，旧名 MAILCLIENT_SIGN_* 回退（已有流水线 / 测试机不用改配置）
        Assert.Contains(
            "foreach ($prefix in @('ZUNGMAIL_SIGN_', 'MAILCLIENT_SIGN_'))",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Get-SignEnvironment 'THUMBPRINT'", script, StringComparison.Ordinal);
        Assert.Contains("Get-SignEnvironment 'PFX'", script, StringComparison.Ordinal);
        Assert.Contains("Get-SignEnvironment 'PFX_PASSWORD'", script, StringComparison.Ordinal);
        Assert.Contains("Get-SignEnvironment 'TIMESTAMP'", script, StringComparison.Ordinal);

        // 没配证书时仍然只提示「未签名」并继续，不阻断打包
        Assert.Contains("本次产出未签名", script, StringComparison.Ordinal);
        Assert.Contains("if ($Required) { throw $message }", script, StringComparison.Ordinal);
    }
}
