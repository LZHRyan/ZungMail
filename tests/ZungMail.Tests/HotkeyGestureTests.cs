using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.Tests;

/// <summary>全局热键字符串的解析与规范化。</summary>
[Trait("Category", "Unit")]
public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+M", "Ctrl+Alt+M")]
    [InlineData("control + shift + f5", "Ctrl+Shift+F5")]
    [InlineData("Alt+0", "Alt+0")]
    [InlineData("Shift+Ctrl+Win+Alt+K", "Ctrl+Alt+Shift+Win+K")]
    [InlineData("  Ctrl+Alt+m  ", "Ctrl+Alt+M")]
    public void 解析_合法组合并规范化显示(string input, string expected)
    {
        Assert.True(HotkeyGesture.TryParse(input, out var gesture));
        Assert.NotNull(gesture);
        Assert.Equal(expected, gesture!.Display);
    }

    [Theory]
    [InlineData("M")]                 // 缺少修饰键
    [InlineData("Ctrl")]              // 缺少主键
    [InlineData("Ctrl+Alt+M+N")]      // 多个主键
    [InlineData("Ctrl+Alt+Escape")]   // 不支持的键
    [InlineData("Ctrl+Alt+F25")]      // F 键越界
    [InlineData("Ctrl+Alt+")]         // 缺少主键
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 解析_拒绝非法输入(string? input)
        => Assert.False(HotkeyGesture.TryParse(input, out var gesture));

    [Fact]
    public void 解析_Ctrl与Control视为同义()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+K", out var first));
        Assert.True(HotkeyGesture.TryParse("Control+K", out var second));
        Assert.Equal(first!.Display, second!.Display);
        Assert.Equal(HotkeyModifiers.Control, first.Modifiers);
    }

    [Fact]
    public void 解析_功能键边界()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+F1", out var f1));
        Assert.Equal("Ctrl+F1", f1!.Display);

        Assert.True(HotkeyGesture.TryParse("Ctrl+F24", out var f24));
        Assert.Equal("Ctrl+F24", f24!.Display);
    }
}
