using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ZungMail.App.Services;
using ZungMail.App.ViewModels;
using ZungMail.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 「查看邮件源码」的界面契约：右键入口、源码窗口结构、默认文件名与视图模型状态文案。
/// 窗口本身不创建（WebView2 / 托盘依赖运行环境），只解析 XAML 与视图模型。
/// </summary>
[Trait("Category", "Ui")]
public sealed class MailSourceUiTests
{
    private static string ViewPath(string name) => Path.Combine(AppContext.BaseDirectory, "Views", name);

    [Fact]
    public void 邮件列表右键菜单提供查看邮件源码()
    {
        var xaml = File.ReadAllText(ViewPath("MainWindow.xaml"));

        Assert.Contains("查看邮件源码", xaml);
        Assert.Contains("Command=\"{Binding ViewMailSourceCommand}\"", xaml);
    }

    [Fact]
    public void 独立阅读窗口邮件头右键同样提供入口()
    {
        var xaml = File.ReadAllText(ViewPath("MailReaderWindow.xaml"));

        Assert.Contains("Header=\"查看邮件源码\"", xaml);
        Assert.Contains("Command=\"{Binding ViewMailSourceCommand}\"", xaml);
    }

    [Fact]
    public void 源码窗口是只读等宽文本并提供复制与另存()
    {
        var xaml = File.ReadAllText(ViewPath("MailSourceWindow.xaml"));

        // 只读 + 不换行 + 双向滚动：源码不能被随手改，也不能折行破坏对齐
        Assert.Contains("IsReadOnly", xaml);
        Assert.Contains("TextWrapping\" Value=\"NoWrap", xaml);
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility", xaml);
        Assert.Contains("Cascadia Mono", xaml);

        // 三个动作 + 状态绑定
        Assert.Contains("复制全部", xaml);
        Assert.Contains("另存为 .eml", xaml);
        Assert.Contains("重新获取", xaml);
        Assert.Contains("{Binding Text, Mode=OneWay}", xaml);
        Assert.Contains("{Binding SourceLabel}", xaml);
        Assert.Contains("{Binding StatusText}", xaml);
    }

    [Theory]
    [InlineData("", "邮件源码.eml")]
    [InlineData("   ", "邮件源码.eml")]
    [InlineData("订舱确认", "订舱确认.eml")]
    [InlineData("RE: 1030954 / MBL", "RE_ 1030954 _ MBL.eml")]
    public void 默认文件名按主题生成并清理非法字符(string subject, string expected)
    {
        Assert.Equal(expected, MailSourceFileWriter.SuggestFileName(subject));
    }

    [Fact]
    public void 默认文件名过长时截断到80字符()
    {
        var name = MailSourceFileWriter.SuggestFileName(new string('主', 200));

        Assert.Equal(80 + ".eml".Length, name.Length);
        Assert.EndsWith(".eml", name);
    }

    [Fact]
    public void 另存按字节原样写盘()
    {
        var root = Path.Combine(Path.GetTempPath(), "zungmail-source-save-" + Guid.NewGuid().ToString("N")[..8]);
        var path = Path.Combine(root, "out.eml");
        var bytes = new byte[] { 0x46, 0x72, 0x6F, 0x6D, 0x3A, 0x20, 0x0D, 0x0A };
        try
        {
            MailSourceFileWriter.Save(bytes, path);

            Assert.True(File.Exists(path));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 视图模型_原始源码显示来源与编码()
    {
        var viewModel = CreateViewModel(MailSourceSnapshotFactory.Raw());

        await viewModel.OpenAsync(9, "订舱确认");

        Assert.Equal("服务器原始源码", viewModel.SourceLabel);
        Assert.False(viewModel.HasFallback);
        Assert.False(viewModel.HasError);
        Assert.False(viewModel.IsLoading);
        Assert.StartsWith("UTF-8 · 1 KB · ", viewModel.SummaryText);
        Assert.Contains("Received:", viewModel.Text);
        Assert.Equal("订舱确认.eml", viewModel.SuggestedFileName);
        Assert.True(viewModel.HasSource);
    }

    [Fact]
    public async Task 视图模型_回退时显示原因与重建徽标()
    {
        var viewModel = CreateViewModel(MailSourceSnapshotFactory.Rebuilt("无法连接服务器"));

        await viewModel.OpenAsync(9, "到港通知");

        Assert.Equal("本地重建源码", viewModel.SourceLabel);
        Assert.True(viewModel.HasFallback);
        Assert.Equal("无法连接服务器", viewModel.FallbackReason);
    }

    [Fact]
    public async Task 视图模型_完全取不到时显示错误并可重试()
    {
        var viewModel = CreateViewModel(null);

        await viewModel.OpenAsync(9, "提箱单");

        Assert.True(viewModel.HasError);
        Assert.Contains("无法获取源码", viewModel.ErrorText);
        Assert.False(viewModel.HasSource);
        Assert.False(viewModel.IsLoading);
        Assert.Equal("获取源码失败", viewModel.StatusText);
    }

    private static MailSourceViewModel CreateViewModel(MailSourceSnapshot? snapshot)
        => new(new StubSourceService(snapshot), NullLogger<MailSourceViewModel>.Instance);

    private sealed class StubSourceService : IMailSourceService
    {
        private readonly MailSourceSnapshot? _snapshot;

        public StubSourceService(MailSourceSnapshot? snapshot) => _snapshot = snapshot;

        public Task<MailSourceSnapshot> LoadAsync(
            long messageId, bool preferServerSource = true, CancellationToken ct = default)
            => _snapshot is null
                ? throw new MailSourceUnavailableException("无法获取源码：服务器不可达，且本地没有正文可重建。")
                : Task.FromResult(_snapshot);
    }

    private static class MailSourceSnapshotFactory
    {
        public static MailSourceSnapshot Raw() => new()
        {
            MessageId = 9,
            Kind = MailSourceKind.ServerRaw,
            Text = "From: sender@example.com\r\nReceived: from mx.example.com\r\n\r\nhello",
            Bytes = new byte[1024],
            EncodingName = "UTF-8"
        };

        public static MailSourceSnapshot Rebuilt(string reason) => new()
        {
            MessageId = 9,
            Kind = MailSourceKind.Rebuilt,
            Text = "Subject: Arrival Notice\r\n\r\nbody",
            Bytes = new byte[512],
            EncodingName = "UTF-8",
            FallbackReason = reason
        };
    }
}
