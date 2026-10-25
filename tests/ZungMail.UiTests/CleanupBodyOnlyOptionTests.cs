using System;
using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 历史邮件清理的「仅清理正文和附件」选项：勾选后不删邮件本身，
/// 只清正文与附件文件，因此按钮文案要跟着切换、说明要写清不可撤销。
/// </summary>
[Trait("Category", "Ui")]
public sealed class CleanupBodyOnlyOptionTests
{
    private static string WindowXaml() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "CleanupWindow.xaml"));

    [Fact]
    public void 左栏提供仅清正文选项且双向绑定()
    {
        var xaml = WindowXaml();

        Assert.Contains("Text=\"清理方式\"", xaml);
        Assert.Contains("Content=\"仅清理正文和附件（保留邮件头）\"", xaml);
        Assert.Contains("{Binding ClearBodyOnly, Mode=TwoWay}", xaml);
    }

    [Fact]
    public void 选项说明写明保留邮件头与不可撤销()
    {
        var xaml = WindowXaml();
        var start = xaml.IndexOf("仅清理正文和附件（保留邮件头）", StringComparison.Ordinal);
        Assert.True(start > 0, "找不到仅清正文选项");

        var block = xaml.Substring(start, Math.Min(700, xaml.Length - start));
        Assert.Contains("不删除邮件本身", block);
        Assert.Contains("附件文件", block);
        Assert.Contains("重新下载", block);
        Assert.Contains("不进入隔离区", block);
        Assert.Contains("不可撤销", block);
    }

    [Fact]
    public void 两个清理按钮文案跟随模式切换()
    {
        var xaml = WindowXaml();

        Assert.Contains("Content=\"{Binding CleanupByFilterText}\"", xaml);
        Assert.Contains("Content=\"{Binding CleanupSelectedText}\"", xaml);
    }

    [Fact]
    public void 预览与审计入口保持不变()
    {
        var xaml = WindowXaml();

        Assert.Contains("Command=\"{Binding PreviewCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding CleanupByFilterCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding CleanupSelectedCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding UndoCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding PurgeCommand}\"", xaml);
    }
}
