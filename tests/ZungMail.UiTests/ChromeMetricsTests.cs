using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 主窗口两条横条的高度：标题栏 32、状态栏 32（原来 38 / 45）。
/// 状态栏原来被 <c>Btn</c> 样式的 MinHeight=32 撑到 45，所以必须用紧凑样式，
/// 而且高度要写死——按钮是条件显示的（待同步 / 撤销删除 / 升级标识），
/// 靠内容撑会在按钮消失时整条变矮、界面跳一下。
/// </summary>
[Trait("Category", "Ui")]
public sealed class ChromeMetricsTests
{
    private static string ReadMainWindow()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml"));

    private static string ReadTheme()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Theme", "Controls.xaml"));

    [Fact]
    public void 标题栏高度32()
    {
        var xaml = ReadMainWindow();
        var start = xaml.IndexOf("============ 标题栏", StringComparison.Ordinal);

        Assert.True(start > 0, "找不到标题栏注释");
        var block = xaml.Substring(start, 400);

        Assert.Contains("Height=\"32\"", block);
        Assert.DoesNotContain("Height=\"38\"", block);
    }

    [Fact]
    public void 状态栏高度固定32且用紧凑按钮()
    {
        var xaml = ReadMainWindow();
        var start = xaml.IndexOf("============ 状态栏", StringComparison.Ordinal);

        Assert.True(start > 0, "找不到状态栏注释");
        var block = xaml.Substring(start, 400);

        Assert.Contains("Height=\"32\"", block);      // 写死，避免按钮显隐导致整条跳动
        Assert.Contains("Padding=\"14,4\"", block);

        // 状态栏里的按钮（升级标识 / 撤销删除 / 待同步 / 存储占用）必须套紧凑样式
        var statusArea = xaml.Substring(start);
        var compact = statusArea.Split("Style=\"{StaticResource StatusBarButton}\"").Length - 1;
        Assert.True(compact >= 4, $"状态栏紧凑按钮只有 {compact} 个，应为 4 个");
    }

    /// <summary>状态栏最右边那截「MailKit · IMAP 993 / SMTP 465」已按用户要求去掉，别再加回来。</summary>
    [Fact]
    public void 状态栏不再显示协议版本尾巴()
    {
        Assert.DoesNotContain("MailKit · IMAP 993 / SMTP 465", ReadMainWindow());
    }

    [Fact]
    public void 紧凑按钮量出来是22高_普通按钮仍是32()
    {
        var (compact, regular) = RunOnStaThread(MeasureButtons);

        Assert.Equal(22, compact);
        Assert.Equal(32, regular);
    }

    private static (double Compact, double Regular) MeasureButtons()
    {
        // 与 ControlMetricsTests 同样的做法：只解析主题源文件、只做 Measure，不创建窗口
        var xaml = ReadTheme().Replace(" x:Shared=\"False\"", string.Empty, StringComparison.Ordinal);
        var dictionary = (ResourceDictionary)XamlReader.Parse(xaml);

        var compact = new Button
        {
            Style = (Style)dictionary["StatusBarButton"],
            Content = new TextBlock { Text = "本地数据库 13317 封", FontSize = 12 }
        };
        var regular = new Button
        {
            Style = (Style)dictionary["Btn"],
            Content = "按钮"
        };

        var panel = new StackPanel();
        panel.Children.Add(compact);
        panel.Children.Add(regular);
        panel.Measure(new Size(320, double.PositiveInfinity));

        return (compact.DesiredSize.Height, regular.DesiredSize.Height);
    }

    private static T RunOnStaThread<T>(Func<T> action) where T : struct
    {
        var result = default(T);
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            throw new InvalidOperationException("控件高度测量超时（STA 线程未结束）");
        }

        if (failure != null)
        {
            throw new InvalidOperationException("控件高度测量失败：" + failure.Message, failure);
        }

        return result;
    }
}
