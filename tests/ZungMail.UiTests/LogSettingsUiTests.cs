using System;
using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 设置 → 通用 →「诊断与日志」的界面约定：保留天数下拉、占用说明与手动清理入口都要在，
/// 否则「日志最多保存 7 天」就只剩后台在跑，用户看不到也改不了。
/// </summary>
public sealed class LogSettingsUiTests
{
    private static string SettingsXamlPath => Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml");

    [Fact]
    public void SettingsScriptIsAvailableToTests()
        => Assert.True(File.Exists(SettingsXamlPath), "缺少设置窗口 XAML 副本：" + SettingsXamlPath);

    [Fact]
    public void GeneralSectionExposesLogRetentionCard()
    {
        var xaml = File.ReadAllText(SettingsXamlPath);

        Assert.Contains("Text=\"诊断与日志\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding LogRetentionOptions}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding LogRetentionIndex, Mode=TwoWay}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding LogRetentionHint}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding LogUsageText}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding CleanLogsCommand}", xaml, StringComparison.Ordinal);
    }
}
