using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>「标为未读」按钮：信封图标右上角要有未读点，点了才知道这动作是「标未读」而不是「已读」。</summary>
[Trait("Category", "Ui")]
public sealed class UnreadDotTests
{
    [Fact]
    public void 信封图标带未读圆点且只在标为未读状态显示()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml");
        Assert.True(File.Exists(path), "缺少主窗口 XAML：" + path);
        var xaml = File.ReadAllText(path);

        // 未读点：主题色小圆 + 只在「标为未读」（邮件已读）时出现
        Assert.Contains("未读点", xaml);
        var dotIndex = xaml.IndexOf("未读点", System.StringComparison.Ordinal);
        Assert.True(dotIndex > 0);

        var tail = xaml.Substring(dotIndex, 900);
        Assert.Contains("<Ellipse", tail);
        Assert.Contains("AccentBrush", tail);
        Assert.Contains("InverseBoolToVisibility", tail);
    }
}
