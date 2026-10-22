using System;
using System.IO;
using System.Reflection;
using ZungMail.App.Services;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 版本标识的位置：主界面标题栏显示 v版本号（原来是「个人版」），
/// 「关于」页只留「个人版」徽标，版本号由 AppVersionInfo 统一提供。
/// </summary>
[Trait("Category", "Ui")]
public sealed class AboutVersionBadgeTests
{
    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));

    private static string SettingsXaml =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml"));

    [Fact]
    public void 标题栏显示版本标识而不是个人版()
    {
        var xaml = MainWindowXaml;

        Assert.Contains("Text=\"{Binding AppVersion}\"", xaml);
        Assert.DoesNotContain("个人版", xaml);
    }

    [Fact]
    public void 关于页只保留个人版徽标()
    {
        var xaml = SettingsXaml;
        var about = xaml[xaml.IndexOf("<!-- ============ 关于 ============ -->", StringComparison.Ordinal)..];

        Assert.Contains("Text=\"个人版\"", about);

        // 版本号搬到标题栏后，关于页不再重复显示带 v 前缀的版本
        Assert.DoesNotContain("StringFormat=v{0}", about);
    }

    [Fact]
    public void 版本号来自程序集信息并带v前缀()
    {
        var informational = typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var expected = informational?.Split('+')[0];

        Assert.False(string.IsNullOrWhiteSpace(AppVersionInfo.Display));
        Assert.Equal("v" + AppVersionInfo.Display, AppVersionInfo.DisplayWithPrefix);

        if (!string.IsNullOrWhiteSpace(expected))
        {
            Assert.Equal(expected, AppVersionInfo.Display);
        }
    }
}
