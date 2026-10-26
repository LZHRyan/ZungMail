using System;
using System.IO;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 发送前检查要能关：默认开启，设置 → 通用 里的勾选决定写信窗口是否弹确认框。
/// 关掉之后 ComposeSendGuard 根本不参与，也不会出现「仍然发送」这类打断。
/// </summary>
[Trait("Category", "Ui")]
public sealed class SendCheckOptionTests
{
    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relativePath));

    [Fact]
    public void 设置项默认开启()
    {
        Assert.True(new AppSettings().SendCheckEnabled);
    }

    [Fact]
    public void 通用设置里有发送前检查勾选()
    {
        var xaml = ReadSource(Path.Combine("Views", "SettingsWindow.xaml"));

        Assert.Contains("发送前检查（漏附件、空主题、收件人过多时先确认一次）", xaml);
        Assert.Contains("IsChecked=\"{Binding Settings.SendCheckEnabled}\"", xaml);
        // 只提示不阻断，这条语义要写在提示里，避免用户以为关掉会漏发
        Assert.Contains("确认后照常发送", xaml);
    }

    [Fact]
    public void 写信窗口按设置决定是否跑检查()
    {
        var source = ReadSource(Path.Combine("Source", "ComposeViewModel.cs"));

        Assert.Contains("_settings.Current.SendCheckEnabled", source);
        Assert.Contains("ComposeSendGuard.Check(", source);
        // 关闭时给空集合，后续 warnings.Count > 0 的确认框自然不弹
        Assert.Contains("Array.Empty<ComposeWarning>()", source);
    }
}
