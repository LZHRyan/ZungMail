using System;
using System.IO;
using ZungMail.App.ViewModels;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 账户 / 文件夹右键菜单（批次 A）：入口要齐、作用域要跟着节点走、
/// 「收取」在停用或配置不完整时要灰掉。
/// </summary>
[Trait("Category", "Ui")]
public sealed class AccountFolderMenuTests
{
    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));

    [Fact]
    public void 账户右键菜单包含收取到设置五个入口()
    {
        var xaml = MainWindowXaml;

        Assert.Contains("Header=\"收取(_G)\"", xaml);
        Assert.Contains("Header=\"新建文件夹(_T)…\"", xaml);
        Assert.Contains("Header=\"整理文件夹(_I)…\"", xaml);
        Assert.Contains("Header=\"导入邮件(_E)…\"", xaml);
        Assert.Contains("Header=\"设置(_P)…\"", xaml);

        // 每个入口都要把被右键的账户作为参数传下去，不能靠「当前选中的账户」
        Assert.Contains("CreateFolderForAccountCommand", xaml);
        Assert.Contains("OpenCleanupForAccountCommand", xaml);
        Assert.Contains("ImportEmlForAccountCommand", xaml);
        Assert.Contains("OpenAccountSettingsCommand", xaml);
    }

    [Fact]
    public void 文件夹右键菜单有整理此文件夹()
    {
        Assert.Contains("Header=\"整理此文件夹…\"", MainWindowXaml);
        Assert.Contains("OpenCleanupForFolderCommand", MainWindowXaml);
    }

    [Fact]
    public void 主窗口所有右键菜单都套了自定义样式()
    {
        // 之前文件夹菜单漏了 MailContextMenu，弹出的是系统默认白框（用户截图反馈过）
        var lines = MainWindowXaml.Split('\n');

        foreach (var line in lines)
        {
            if (!line.Contains("<ContextMenu")) continue;
            Assert.Contains("Style=", line);
        }
    }

    [Fact]
    public void 停用账户不能收取()
    {
        var node = new AccountNode(new Account
        {
            Id = 1,
            Name = "工作邮箱",
            Email = "me@example.com",
            Username = "me@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com",
            IsEnabled = false
        });

        Assert.False(node.CanSync);
        Assert.Contains("停止使用", node.SyncHint);
    }

    [Fact]
    public void 配置不完整的账户不能收取()
    {
        var node = new AccountNode(new Account
        {
            Id = 2,
            Name = "备用邮箱",
            Email = "backup@example.com",
            Username = "backup@example.com",
            ImapHost = "",
            SmtpHost = "smtp.example.com",
            IsEnabled = true
        });

        Assert.False(node.CanSync);
        Assert.Contains("配置不完整", node.SyncHint);
    }

    [Fact]
    public void 正常账户可以收取()
    {
        var node = new AccountNode(new Account
        {
            Id = 3,
            Name = "主账户",
            Email = "main@example.com",
            Username = "main@example.com",
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com",
            IsEnabled = true
        });

        Assert.True(node.CanSync);
        Assert.Contains("立即收取", node.SyncHint);
    }
}
