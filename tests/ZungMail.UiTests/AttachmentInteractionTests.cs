using System;
using System.IO;
using System.Windows;
using Xunit;
using ZungMail.App.Services;

namespace ZungMail.UiTests;

/// <summary>
/// 附件交互：单击只选中、双击才打开预览；右键菜单里的「复制」等价于 Ctrl+C
/// （把附件文件放进剪贴板，可粘到资源管理器 / 聊天窗口 / 新邮件）。
/// </summary>
[Trait("Category", "Ui")]
public sealed class AttachmentInteractionTests
{
    private static string View(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", name));

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("MailReaderWindow.xaml")]
    public void 附件卡片双击打开_单击不再直接触发预览(string view)
    {
        var xaml = View(view);

        // 双击与回车都走预览命令
        Assert.Contains("MouseAction=\"LeftDoubleClick\"", xaml);
        Assert.Contains("PreviewAttachmentCommand", xaml);

        // 卡片按钮本身不能再挂 Command：那是「单击即预览」的写法，双击之前就会误触。
        // 只看 Button 开始标签里的属性，菜单项里的 PlacementTarget.Tag.PreviewAttachmentCommand 不算。
        var cardStart = xaml.IndexOf("Style=\"{StaticResource AttachmentCard}\"", StringComparison.Ordinal);
        Assert.True(cardStart > 0, $"{view} 找不到附件卡片");

        var tagEnd = xaml.IndexOf('>', cardStart);
        var startTag = xaml.Substring(cardStart, tagEnd - cardStart);
        Assert.DoesNotContain("Command=", startTag);
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("MailReaderWindow.xaml")]
    public void 附件右键菜单有复制_并标注CtrlC(string view)
    {
        var xaml = View(view);

        var start = xaml.IndexOf("Header=\"复制\"", StringComparison.Ordinal);
        Assert.True(start > 0, $"{view} 缺少「复制」菜单项");

        var block = xaml.Substring(start, Math.Min(600, xaml.Length - start));
        Assert.Contains("InputGestureText=\"Ctrl+C\"", block);
        Assert.Contains("CopyAttachmentCommand", block);
    }

    [Fact]
    public void 复制载荷同时含文件与路径文本()
    {
        var path = Path.Combine(Path.GetTempPath(), "报价.pdf");

        var data = AttachmentClipboard.BuildDataObject(path);

        Assert.True(data.GetDataPresent(DataFormats.FileDrop));
        var files = data.GetFileDropList();
        Assert.Single(files);
        Assert.Equal(path, files[0]);

        // 纯文本路径：粘到聊天窗口 / 记事本也能用
        Assert.Equal(path, data.GetData(DataFormats.UnicodeText) as string);
    }
}
