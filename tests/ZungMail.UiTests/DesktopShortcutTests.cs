using System;
using System.IO;
using ZungMail.App.Services;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 桌面快捷方式兜底：路径推导与设置页入口。
/// 真正的 <c>WScript.Shell</c> 调用会写用户桌面，不在单测里做（避免覆盖用户已有的快捷方式）；
/// 这里只守住路径规则与界面绑定，COM 调用本身用一次实机点击验证。
/// </summary>
public sealed class DesktopShortcutTests
{
    [Fact]
    public void DefaultPath_IsZungMailLinkOnCurrentUserDesktop()
    {
        var path = DesktopShortcutService.DefaultDesktopLinkPath();

        Assert.EndsWith(DesktopShortcutService.LinkFileName, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ZungMail.lnk", DesktopShortcutService.LinkFileName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ZungMail.lnk"),
            path);
        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public void SettingsWindow_ExposesCreateShortcutButton()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Views", "SettingsWindow.xaml"));

        Assert.Contains("Text=\"桌面快捷方式\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding CreateDesktopShortcutCommand}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ShortcutStatusText}", xaml, StringComparison.Ordinal);
    }
}
