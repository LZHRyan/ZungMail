using System;
using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 历史邮件清理的匹配列表：分页 + 单页/整批勾选。
/// 列表一页 50 条，「全选本页 / 反选本页」和「全选 / 反选」必须都在，
/// 分页按钮绑 PageInfo 与 CanGoPrevious / CanGoNext。
/// </summary>
[Trait("Category", "Ui")]
public sealed class CleanupPagingTests
{
    private static string WindowXaml() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "CleanupWindow.xaml"));

    [Fact]
    public void 列表底栏提供单页与整批勾选()
    {
        var xaml = WindowXaml();

        Assert.Contains("Content=\"全选本页\"", xaml);
        Assert.Contains("Content=\"反选本页\"", xaml);
        Assert.Contains("Content=\"全选\"", xaml);
        Assert.Contains("Content=\"反选\"", xaml);

        Assert.Contains("Command=\"{Binding SelectPageCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding InvertPageCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding SelectAllCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding InvertAllCommand}\"", xaml);
    }

    [Fact]
    public void 分页按钮与页码信息齐备()
    {
        var xaml = WindowXaml();

        Assert.Contains("Command=\"{Binding FirstPageCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding PreviousPageCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding NextPageCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding LastPageCommand}\"", xaml);
        Assert.Contains("Text=\"{Binding PageInfo}\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding CanGoPrevious}\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding CanGoNext}\"", xaml);
    }

    [Fact]
    public void 清理选中按钮跟随勾选状态启用()
    {
        var xaml = WindowXaml();

        Assert.Contains("Content=\"{Binding CleanupSelectedText}\"", xaml);
        Assert.Contains("Command=\"{Binding CleanupSelectedCommand}\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding CanCleanupSelected}\"", xaml);
        Assert.Contains("Text=\"{Binding SelectedRows, StringFormat=已勾选 {0} 封}\"", xaml);
    }

    [Fact]
    public void 行勾选只认点击事件_不回读虚拟化列表状态()
    {
        var xaml = WindowXaml();

        Assert.Contains("Click=\"OnPreviewItemClick\"", xaml);
        Assert.DoesNotContain("OnPreviewItemChecked", xaml);
    }

    [Fact]
    public void 勾选与分页按钮共处同一底栏_不占用筛选左栏()
    {
        var xaml = WindowXaml();
        var start = xaml.IndexOf("仅清理正文和附件（保留邮件头）", StringComparison.Ordinal);
        var end = xaml.IndexOf("仅清理正文和附件（保留邮件头）", StringComparison.Ordinal) > 0
            ? xaml.IndexOf("预览匹配", StringComparison.Ordinal)
            : -1;
        Assert.True(start > 0 && end > start, "找不到清理方式区块");

        // 左栏只放筛选与清理入口，勾选/分页都在匹配列表底栏
        var leftBlock = xaml.Substring(start, end - start);
        Assert.DoesNotContain("SelectPageCommand", leftBlock);
        Assert.DoesNotContain("FirstPageCommand", leftBlock);
    }
}
