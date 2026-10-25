using System;
using System.IO;
using ZungMail.App.Services;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 正文图片双击放大：三层结构回归。
///
/// 1) 注入脚本必须真的监听 dblclick 并把 IMG 地址回传（脚本被删/改坏时双击会静默失效）；
/// 2) 正文控件必须打开消息通道、在导航完成后注入、并把消息落到查看窗口；
/// 3) 查看窗口 XAML 必须有图片区、缩放按钮与另存为入口。
/// </summary>
[Trait("Category", "Ui")]
public sealed class MailImageDoubleClickTests
{
    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relativePath));

    [Fact]
    public void 注入脚本监听双击并回传图片地址()
    {
        var script = MailBodyImageScript.Inject;

        Assert.Contains("dblclick", script);
        Assert.Contains("'IMG'", script);
        Assert.Contains("chrome.webview.postMessage", script);
        Assert.Contains(MailBodyImageMessage.TypeName, script);
        Assert.Contains(MailBodyImageScript.Marker, script);
        // 重复注入要短路，否则每封邮件叠一个监听器
        Assert.Contains("if (window." + MailBodyImageScript.Marker + ")", script);
    }

    [Fact]
    public void 正文控件打开消息通道并在导航完成后注入()
    {
        var source = ReadSource(Path.Combine("Source", "MailBodyView.xaml.cs"));

        Assert.Contains("IsWebMessageEnabled = true", source);
        Assert.Contains("WebMessageReceived += OnWebMessageReceived", source);
        Assert.Contains("NavigationCompleted += OnNavigationCompleted", source);
        Assert.Contains("MailBodyImageScript.Inject", source);
        Assert.Contains("new MailImageViewerWindow(", source);
        // 正文脚本整体仍然禁用：邮件自带脚本不许执行
        Assert.Contains("IsScriptEnabled = false", source);
    }

    [Fact]
    public void 图片右键菜单有查看大图且与双击共用入口()
    {
        var source = ReadSource(Path.Combine("Source", "MailBodyView.xaml.cs"));

        Assert.Contains("查看大图", source);
        // 双击与右键都走 OpenImageViewerAsync，不允许两套实现各写一份
        Assert.Contains("OpenImageViewerAsync(", source);
        Assert.Contains("await OpenImageViewerAsync(uri, message.AltText)", source);
        Assert.Contains("OpenImageViewerAsync(imageUri", source);
        // 原有两条图片操作不能被挤掉
        Assert.Contains("图片另存为…", source);
        Assert.Contains("复制图片", source);
    }

    [Fact]
    public void 查看窗口有图片区缩放与另存为()
    {
        var xaml = ReadSource(Path.Combine("Views", "MailImageViewerWindow.xaml"));

        Assert.Contains("x:Name=\"Preview\"", xaml);
        Assert.Contains("x:Name=\"Scroller\"", xaml);
        Assert.Contains("适应窗口", xaml);
        Assert.Contains("实际大小", xaml);
        Assert.Contains("另存为", xaml);
        Assert.Contains("MouseWheel=\"OnViewerMouseWheel\"", xaml);
        // 标题栏与主窗口同为 32 DIP
        Assert.Contains("<RowDefinition Height=\"32\" />", xaml);
    }
}
