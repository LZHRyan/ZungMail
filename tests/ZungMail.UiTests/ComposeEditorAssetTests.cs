using System;
using System.IO;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 撰写页素材回归：回复 / 转发的引用块要在编辑器里有样式，且光标落在引用上方。
/// 这两处只在 WebView2 页面里生效，普通控件测试看不到，直接解析源文件守住。
/// </summary>
[Trait("Category", "Ui")]
public sealed class ComposeEditorAssetTests
{
    private static string AssetsRoot => Path.Combine(AppContext.BaseDirectory, "Assets", "editor");

    [Fact]
    public void 编辑器样式包含引用块与深层引用压平规则()
    {
        var css = File.ReadAllText(Path.Combine(AssetsRoot, "editor.css"));

        Assert.Contains("#editor .mc-quote", css);
        Assert.Contains("#editor .mc-quote-head", css);
        Assert.Contains("#editor .mc-quote .q-deep", css);
    }

    [Fact]
    public void 编辑器脚本在带引用块时把光标放到引用上方()
    {
        var js = File.ReadAllText(Path.Combine(AssetsRoot, "editor.js"));

        Assert.Contains("function placeCaretAtStart(", js);
        Assert.Contains("if (editor.querySelector('.mc-quote')) placeCaretAtStart(editor);", js);
    }
}
