using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>「全部搜索条件」面板：Foxmail 的 9 个条件都要在，且不再退化成只有 4 个字段的旧版。</summary>
[Trait("Category", "Ui")]
public sealed class SearchFilterPanelTests
{
    private static string ReadMainWindow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml");
        Assert.True(File.Exists(path), "缺少主窗口 XAML：" + path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void 面板含发件人收件人主题全文附件标签提醒备注时间()
    {
        var xaml = ReadMainWindow();

        Assert.Contains("Text=\"发件人：\"", xaml);
        Assert.Contains("Text=\"收件人：\"", xaml);
        Assert.Contains("Text=\"主题：\"", xaml);
        Assert.Contains("Text=\"全文：\"", xaml);
        Assert.Contains("Text=\"附件：\"", xaml);
        Assert.Contains("Text=\"标签：\"", xaml);
        Assert.Contains("Text=\"提醒：\"", xaml);
        Assert.Contains("Text=\"备注：\"", xaml);
        Assert.Contains("Text=\"时间：\"", xaml);

        // 下拉项：附件 有/无、提醒 有/无、时间 6 档 + 自定义
        Assert.Contains("Content=\"有附件\"", xaml);
        Assert.Contains("Content=\"无附件\"", xaml);
        Assert.Contains("Content=\"有提醒\"", xaml);
        Assert.Contains("Content=\"无提醒\"", xaml);
        foreach (var label in new[] { "时间不限", "今天", "三天内", "一周内", "一个月内", "一年内", "自定义" })
        {
            Assert.Contains($"Content=\"{label}\"", xaml);
        }

        Assert.Contains("{Binding SearchFilterTagOptions}", xaml);
        Assert.Contains("{Binding IsSearchFilterCustomRange", xaml);
    }
}
