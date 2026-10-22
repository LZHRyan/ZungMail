using System;
using System.IO;
using System.Text.RegularExpressions;
using ZungMail.App.Services;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>内置离线使用教程：路径、自包含、章节覆盖、缺失提示与设置入口。</summary>
[Trait("Category", "Ui")]
public sealed class UserGuideTests
{
    private static readonly string[] RequiredSections =
    {
        "start", "account", "interface", "reading", "compose", "search",
        "organize", "freight", "todo", "data", "settings", "shortcuts", "faq"
    };

    [Fact]
    public void 路径解析_始终指向程序目录下的教程文件()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "zungmail-guide-path");
        var expected = Path.GetFullPath(Path.Combine(baseDirectory, @"Assets\help\user-guide.html"));

        Assert.Equal(expected, UserGuide.ResolvePath(baseDirectory));
    }

    [Fact]
    public void 教程随程序复制_并包含必需章节且不引用外部资源()
    {
        var path = UserGuide.ResolvePath(AppContext.BaseDirectory);
        Assert.True(File.Exists(path), "教程未复制到输出目录：" + path);

        var html = File.ReadAllText(path);
        Assert.Contains("ZungMail 使用教程", html);
        foreach (var section in RequiredSections)
        {
            Assert.Contains($"id=\"{section}\"", html);
        }

        // 教程必须离线可读：不允许通过 src/href 指向 http(s) 资源。
        Assert.False(
            Regex.IsMatch(html, @"(?is)(?:src|href)\s*=\s*[""']https?://"),
            "教程不应引用外部网络资源");
        Assert.Contains("<style>", html);
        Assert.Contains("window.print()", html);
    }

    [Fact]
    public void 教程缺失时_返回包含路径的错误且不调用打开器()
    {
        var root = Path.Combine(Path.GetTempPath(), "zungmail-guide-missing-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var opened = false;
            Assert.False(UserGuide.TryOpen(root, out var error, _ => opened = true));
            Assert.False(opened);
            Assert.Contains("找不到使用教程文件", error);
            Assert.Contains("Assets", error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 教程存在时_把完整路径交给打开器()
    {
        var root = Path.Combine(Path.GetTempPath(), "zungmail-guide-open-" + Guid.NewGuid().ToString("N")[..8]);
        var guide = Path.Combine(root, "Assets", "help", "user-guide.html");
        Directory.CreateDirectory(Path.GetDirectoryName(guide)!);
        File.WriteAllText(guide, "<html><body>教程</body></html>");
        try
        {
            string? opened = null;
            Assert.True(UserGuide.TryOpen(root, out var error, path => opened = path));
            Assert.Equal("", error);
            Assert.Equal(Path.GetFullPath(guide), opened);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 教程写明新数据目录与旧目录自动接管()
    {
        var path = UserGuide.ResolvePath(AppContext.BaseDirectory);
        Assert.True(File.Exists(path), "教程未复制到输出目录：" + path);

        var html = File.ReadAllText(path);

        // 改名后数据目录位置变了：教程必须写新路径，并说明旧目录会被自动接管，避免用户手工搬文件
        Assert.Contains(@"%APPDATA%\ZungMail", html);
        Assert.Contains("%APPDATA%\\MailClient", html);
        Assert.Contains("自动接管", html);
    }

    [Fact]
    public void 教程说明查看邮件源码入口与来源差别()
    {
        var path = UserGuide.ResolvePath(AppContext.BaseDirectory);
        Assert.True(File.Exists(path), "教程未复制到输出目录：" + path);

        var html = File.ReadAllText(path);

        Assert.Contains("查看邮件源码", html);
        Assert.Contains("原始 MIME", html);
        // 回退与另存两条关键说明不能丢
        Assert.Contains("本地重建", html);
        Assert.Contains("另存为 .eml", html);
    }

    [Fact]
    public void 设置关于页_保留打开使用教程入口()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml");
        Assert.True(File.Exists(path), "缺少设置页 XAML：" + path);

        var xaml = File.ReadAllText(path);
        Assert.Contains("OpenUserGuideCommand", xaml);
        Assert.Contains("打开使用教程", xaml);
        Assert.Contains("使用教程", xaml);
    }

    [Fact]
    public void 设置通用页_提供默认折叠超长邮件历史引用的开关与保留层数()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml");
        Assert.True(File.Exists(path), "缺少设置页 XAML：" + path);

        var xaml = File.ReadAllText(path);
        Assert.Contains("默认折叠超长邮件的历史引用", xaml);
        Assert.Contains("{Binding FoldQuotedHistoryEnabled}", xaml);
        Assert.Contains("{Binding FoldKeepLayerOptions}", xaml);
        Assert.Contains("{Binding FoldKeepLayerIndex, Mode=TwoWay}", xaml);
    }
}
