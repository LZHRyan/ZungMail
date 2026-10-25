using System;
using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 右侧「往来邮件」卡片：主题 / 正文摘要 / 带时分的日期 / 附件图标，
/// 未读用左侧主色竖标 + 加粗主题区分（原来只有一行主题 + 日期，太素）。
/// </summary>
[Trait("Category", "Ui")]
public sealed class CorrespondenceCardTests
{
    private static string CardBlock()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));
        var start = xaml.IndexOf("往来邮件", StringComparison.Ordinal);
        var end = xaml.IndexOf("该发件人的附件", StringComparison.Ordinal);

        Assert.True(start > 0 && end > start, "找不到往来邮件卡片区块");
        return xaml.Substring(start, end - start);
    }

    [Fact]
    public void 卡片包含主题_摘要_日期与附件标记()
    {
        var block = CardBlock();

        Assert.Contains("Binding SubjectDisplay", block);
        Assert.Contains("Binding PreviewDisplay", block);      // 正文摘要
        Assert.Contains("Binding HasPreview", block);          // 没摘要就不留空行
        Assert.Contains("Binding DateDetailDisplay", block);   // 带时分的日期
        Assert.Contains("IcoPaperclip", block);                // 附件小图标
        Assert.Contains("Binding HasAttachment", block);
    }

    [Fact]
    public void 未读用左侧主色竖标与加粗主题()
    {
        var block = CardBlock();

        Assert.Contains("Binding IsUnread", block);
        Assert.Contains("Width=\"3\"", block);                 // 竖标宽度
        Assert.Contains("DataTrigger Binding=\"{Binding IsUnread}\" Value=\"True\"", block);
        Assert.Contains("FontWeight\" Value=\"SemiBold\"", block);
    }

    [Fact]
    public void 卡片内边距交给模板_避免竖标被容器Padding挤进去()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));
        var index = xaml.IndexOf("x:Key=\"CorrespondenceCardItem\"", StringComparison.Ordinal);

        Assert.True(index > 0, "找不到 CorrespondenceCardItem 样式");
        Assert.Contains("Padding\" Value=\"0\"", xaml.Substring(index, 200));
    }
}
