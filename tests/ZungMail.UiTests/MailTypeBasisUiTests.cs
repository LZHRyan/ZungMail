using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 类型依据的界面契约：两处类型徽标都要把「为什么判成这一类」挂在 ToolTip 上，
/// 让用户能看懂命中的词或「继承会话类型」。
/// </summary>
[Trait("Category", "Ui")]
public sealed class MailTypeBasisUiTests
{
    private static string ViewPath(string name) => Path.Combine(AppContext.BaseDirectory, "Views", name);

    [Fact]
    public void 主窗口详情类型徽标带依据提示()
    {
        var xaml = File.ReadAllText(ViewPath("MainWindow.xaml"));
        Assert.Contains("ToolTip=\"{Binding Detail.IndustryTypeHint}\"", xaml);
        Assert.Contains("IndustryTypeLabel", xaml);
    }

    [Fact]
    public void 独立阅读窗口类型徽标带依据提示()
    {
        var xaml = File.ReadAllText(ViewPath("MailReaderWindow.xaml"));
        Assert.Contains("ToolTip=\"{Binding Detail.IndustryTypeHint}\"", xaml);
    }
}
