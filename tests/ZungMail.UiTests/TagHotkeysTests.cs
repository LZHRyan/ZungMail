using System.Windows.Input;
using ZungMail.App.Services;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>标签快捷键：WPF 单键 → 键位 token（1-9 / A-Z / F2-F12），修饰键组合不参与。</summary>
[Trait("Category", "Unit")]
public class TagHotkeysTests
{
    [Theory]
    [InlineData(Key.A, "A")]
    [InlineData(Key.Z, "Z")]
    [InlineData(Key.D1, "1")]
    [InlineData(Key.D9, "9")]
    [InlineData(Key.NumPad7, "7")]
    [InlineData(Key.F2, "F2")]
    [InlineData(Key.F12, "F12")]
    public void 单键映射到键位(Key key, string expected)
    {
        Assert.True(TagHotkeys.TryGetToken(key, out var token));
        Assert.Equal(expected, token);
    }

    [Theory]
    [InlineData(Key.F5)]      // 刷新键，保留给应用
    [InlineData(Key.F1)]      // 预留帮助
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.OemComma)]
    [InlineData(Key.Escape)]
    [InlineData(Key.Space)]
    [InlineData(Key.Enter)]
    public void 保留键与不支持的键返回失败(Key key)
        => Assert.False(TagHotkeys.TryGetToken(key, out _));

    [Fact]
    public void 可绑定键位集合不含修饰键组合()
    {
        Assert.Contains("1", TagShortcutKeys.All);
        Assert.Contains("A", TagShortcutKeys.All);
        Assert.Contains("F12", TagShortcutKeys.All);
        Assert.DoesNotContain("F5", TagShortcutKeys.All);
        Assert.DoesNotContain("F1", TagShortcutKeys.All);
        Assert.DoesNotContain(TagShortcutKeys.All, key => key.Contains('+'));
    }
}
