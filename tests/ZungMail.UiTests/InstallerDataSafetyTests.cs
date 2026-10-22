using System;
using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 安装包的数据安全回归：覆盖升级只替换程序文件，
/// 安装段不得删除数据目录（`%APPDATA%\ZungMail` / `%LOCALAPPDATA%\ZungMail`），
/// 只有卸载段才能在用户二次确认后删除。
/// </summary>
public sealed class InstallerDataSafetyTests
{
    private static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "Packaging", "ZungMail.nsi");

    [Fact]
    public void ScriptIsAvailableToTests()
        => Assert.True(File.Exists(ScriptPath), "缺少打包脚本副本：" + ScriptPath);

    [Fact]
    public void InstallSectionNeverDeletesUserData()
    {
        var script = File.ReadAllText(ScriptPath);
        var installStart = script.IndexOf("Section \"ZungMail 主程序\"", StringComparison.Ordinal);
        var uninstallStart = script.IndexOf("Section \"Uninstall\"", StringComparison.Ordinal);

        Assert.True(installStart > 0, "找不到安装段");
        Assert.True(uninstallStart > installStart, "找不到卸载段");

        var installSection = script[installStart..uninstallStart];

        // 安装/覆盖升级阶段绝不能出现删除用户数据的语句
        Assert.DoesNotContain("RMDir /r \"$APPDATA", installSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RMDir /r \"$LOCALAPPDATA", installSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Delete \"$APPDATA", installSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Delete \"$LOCALAPPDATA", installSection, StringComparison.OrdinalIgnoreCase);

        // 覆盖安装沿用上次安装目录，而不是把新版本装到别处
        Assert.Contains("InstallDirRegKey HKLM", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallSectionKeepsDataUnlessUserConfirms()
    {
        var script = File.ReadAllText(ScriptPath);
        var uninstallStart = script.IndexOf("Section \"Uninstall\"", StringComparison.Ordinal);
        Assert.True(uninstallStart > 0);

        var uninstallSection = script[uninstallStart..];

        Assert.Contains("是否同时删除本机的邮件数据库", uninstallSection, StringComparison.Ordinal);
        // IDNO 分支必须跳过删除
        Assert.Contains("IDNO keepUserData", uninstallSection, StringComparison.Ordinal);
        Assert.Contains("keepUserData:", uninstallSection, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerDoesNotShipDataDirectory()
    {
        // 安装源必须来自 publish 输出，绝不能把 data 目录打进安装包
        var script = File.ReadAllText(ScriptPath);
        Assert.Contains("File /r \"${SOURCE}\\*.*\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\\data\\", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallSectionCleansUpLegacyMailClientInstall()
    {
        var script = File.ReadAllText(ScriptPath);
        var installSection = InstallSection(script);

        // 新旧两个主程序都要结束：从 MailClient 升级上来时旧进程可能还开着
        Assert.Matches(@"!define\s+APP_EXE\s+""ZungMail\.exe""", script);
        Assert.Matches(@"!define\s+LEGACY_APP_EXE\s+""MailClient\.exe""", script);
        Assert.Contains("taskkill /IM ${APP_EXE}", installSection, StringComparison.Ordinal);
        Assert.Contains("taskkill /IM ${LEGACY_APP_EXE}", installSection, StringComparison.Ordinal);

        // 旧 NSIS 卸载键必须删掉，否则「应用和功能」里会同时躺着 MailClient 与 ZungMail
        Assert.Contains("Uninstall\\${LEGACY_APP_NAME}", script, StringComparison.Ordinal);
        Assert.Contains("DeleteRegKey HKLM \"${LEGACY_UNINST_KEY}\"", installSection, StringComparison.Ordinal);

        // 旧开始菜单程序组与桌面快捷方式
        Assert.Contains("Delete \"$SMPROGRAMS\\${LEGACY_APP_NAME}\\*.lnk\"", installSection, StringComparison.Ordinal);
        Assert.Contains("RMDir \"$SMPROGRAMS\\${LEGACY_APP_NAME}\"", installSection, StringComparison.Ordinal);
        Assert.Contains("Delete \"$DESKTOP\\${LEGACY_APP_NAME}.lnk\"", installSection, StringComparison.Ordinal);

        // 旧安装目录只在确认里面确实有 MailClient.exe 时才整目录删除，避免误删同名目录
        Assert.Contains(
            "${If} ${FileExists} \"$PROGRAMFILES64\\${LEGACY_APP_NAME}\\${LEGACY_APP_EXE}\"",
            installSection,
            StringComparison.Ordinal);
        Assert.Contains("RMDir /r \"$PROGRAMFILES64\\${LEGACY_APP_NAME}\"", installSection, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallSectionNeverDeletesAnyUserDataDirectory()
    {
        var installSection = InstallSection(File.ReadAllText(ScriptPath));

        // 新旧两种品牌的数据目录在安装/覆盖升级阶段都不能删：
        // 旧目录里可能还有用户从 MailClient 时代留下来的数据备份
        var dataDirectories = new[]
        {
            "$APPDATA\\ZungMail",
            "$LOCALAPPDATA\\ZungMail",
            "$APPDATA\\MailClient",
            "$LOCALAPPDATA\\MailClient",
            "$APPDATA\\${LEGACY_APP_NAME}",
            "$LOCALAPPDATA\\${LEGACY_APP_NAME}",
        };

        foreach (var directory in dataDirectories)
        {
            Assert.DoesNotContain("RMDir /r \"" + directory, installSection, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RMDir \"" + directory, installSection, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Delete \"" + directory, installSection, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UninstallSectionOnlyOffersToDeleteZungMailData()
    {
        var script = File.ReadAllText(ScriptPath);
        var uninstallStart = script.IndexOf("Section \"Uninstall\"", StringComparison.Ordinal);
        Assert.True(uninstallStart > 0);

        var uninstallSection = script[uninstallStart..];

        Assert.Contains("$APPDATA\\ZungMail", uninstallSection, StringComparison.Ordinal);
        Assert.Contains("$LOCALAPPDATA\\ZungMail", uninstallSection, StringComparison.Ordinal);

        // 文案里要明确：旧版 MailClient 的目录不会被自动删除（用户可能需要手动清理）
        Assert.Contains("旧版 MailClient 目录（$APPDATA\\MailClient、$LOCALAPPDATA\\MailClient）不会被自动删除", uninstallSection, StringComparison.Ordinal);
        Assert.Contains("RMDir /r \"$APPDATA\\ZungMail\"", uninstallSection, StringComparison.Ordinal);
        Assert.Contains("RMDir /r \"$LOCALAPPDATA\\ZungMail\"", uninstallSection, StringComparison.Ordinal);
    }

    /// <summary>取出「ZungMail 主程序」安装段（到桌面快捷方式段为止）。</summary>
    private static string InstallSection(string script)
    {
        var installStart = script.IndexOf("Section \"ZungMail 主程序\"", StringComparison.Ordinal);
        var desktopStart = script.IndexOf("Section \"桌面快捷方式\"", StringComparison.Ordinal);
        Assert.True(installStart > 0 && desktopStart > installStart, "找不到主安装段");
        return script[installStart..desktopStart];
    }
}
