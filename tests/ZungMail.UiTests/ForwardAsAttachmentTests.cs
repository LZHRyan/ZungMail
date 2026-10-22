using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>「作为附件转发」入口：主窗口右键菜单 / 阅读工具栏、独立阅读窗口都要有。</summary>
[Trait("Category", "Ui")]
public sealed class ForwardAsAttachmentTests
{
    [Fact]
    public void 主窗口_邮件行右键与阅读工具栏都有作为附件转发()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml");
        Assert.True(File.Exists(path), "缺少主窗口 XAML：" + path);

        var xaml = File.ReadAllText(path);
        Assert.Contains("作为附件转发", xaml);
        Assert.Equal(2, xaml.Split("ForwardAsAttachmentCommand").Length - 1);
    }

    [Fact]
    public void 独立阅读窗口_操作条有作为附件转发()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "MailReaderWindow.xaml");
        Assert.True(File.Exists(path), "缺少阅读窗口 XAML：" + path);

        var xaml = File.ReadAllText(path);
        Assert.Contains("ForwardAsAttachmentCommand", xaml);
        Assert.Contains("作为附件转发", xaml);
    }
}
