using System;
using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 设置 → 关于 的「运行环境（最低要求）」卡片：装机前要核对的系统、内存、WebView2 等要求，
/// 以及一行本机实测（.NET 运行时 + 架构 + WebView2 版本）。
/// </summary>
[Trait("Category", "Ui")]
public sealed class AboutEnvironmentTests
{
    private static string SettingsXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml"));

    private static string About =>
        SettingsXaml[SettingsXaml.IndexOf("<!-- ============ 关于 ============ -->", StringComparison.Ordinal)..];

    [Fact]
    public void 关于页有运行环境卡片()
    {
        Assert.Contains("Text=\"运行环境（最低要求）\"", About);
    }

    [Fact]
    public void 列出关键最低要求()
    {
        var xaml = About;

        Assert.Contains("Windows 10（1607 及以上）", xaml);
        Assert.Contains("x64 双核", xaml);
        Assert.Contains("4 GB 及以上", xaml);
        Assert.Contains("1280 × 800", xaml);
        Assert.Contains(".NET 8 运行时", xaml);
        Assert.Contains("WebView2 运行时", xaml);
    }

    [Fact]
    public void 附带本机实测行()
    {
        Assert.Contains("Text=\"{Binding LocalEnvironmentText}\"", About);
    }
}
