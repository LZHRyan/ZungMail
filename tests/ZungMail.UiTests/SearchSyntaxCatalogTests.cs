using System;
using System.IO;
using System.Linq;
using ZungMail.App.Services;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>搜索框 @ 语法选择器：目录过滤、触发边界、文本替换与主窗口入口。</summary>
[Trait("Category", "Ui")]
public sealed class SearchSyntaxCatalogTests
{
    [Fact]
    public void 目录_覆盖常用语法且Token不重复()
    {
        Assert.True(SearchSyntaxCatalog.All.Count >= 18);
        Assert.Equal(
            SearchSyntaxCatalog.All.Count,
            SearchSyntaxCatalog.All.Select(item => item.Token).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.Contains(SearchSyntaxCatalog.All, item => item.Token == "from:");
        Assert.Contains(SearchSyntaxCatalog.All, item => item.Token == "booking:");
        Assert.Contains(SearchSyntaxCatalog.All, item => item.Token == "has:attachment");
        Assert.Contains(SearchSyntaxCatalog.All, item => item.Token == "type:si");
        Assert.Contains(SearchSyntaxCatalog.All, item => item.Token == "after:7d");
    }

    [Theory]
    [InlineData("", "发件人", "from:")]
    [InlineData("附件", "含附件", "has:attachment")]
    [InlineData("ctr", "箱号", "ctr:")]
    [InlineData("未读", "未读邮件", "is:unread")]
    [InlineData("VGM", "VGM", "type:vgm")]
    [InlineData("时间", "最近 7 天", "after:7d")]
    public void 过滤_按中文标题或Token命中(string query, string expectedTitle, string expectedToken)
    {
        var matches = SearchSyntaxCatalog.Filter(query);

        Assert.Contains(matches, item => item.Title == expectedTitle && item.Token == expectedToken);
    }

    [Fact]
    public void 过滤_空关键词返回完整目录_无法匹配时返回空()
    {
        Assert.Equal(SearchSyntaxCatalog.All.Count, SearchSyntaxCatalog.Filter("").Count);
        Assert.Empty(SearchSyntaxCatalog.Filter("不存在的语法关键词"));
    }

    [Fact]
    public void 触发检测_空白后的半角或全角at()
    {
        Assert.True(SearchSyntaxCatalog.TryGetTrigger("foo @fr", 7, out var start, out var query));
        Assert.Equal(4, start);
        Assert.Equal("fr", query);

        Assert.True(SearchSyntaxCatalog.TryGetTrigger("@附件", 3, out start, out query));
        Assert.Equal(0, start);
        Assert.Equal("附件", query);

        Assert.True(SearchSyntaxCatalog.TryGetTrigger("查询 ＠vgm", 7, out start, out query));
        Assert.Equal(3, start);
        Assert.Equal("vgm", query);
    }

    [Fact]
    public void 触发检测_邮箱地址里的at不弹菜单()
    {
        Assert.False(SearchSyntaxCatalog.TryGetTrigger("ops@example.com", 15, out _, out _));
        Assert.False(SearchSyntaxCatalog.TryGetTrigger("from:ops@example.com", 20, out _, out _));
    }

    [Fact]
    public void 插入_替换触发片段并保留后续搜索词()
    {
        var from = SearchSyntaxCatalog.All.Single(item => item.Token == "from:");
        var (text, caret) = SearchSyntaxCatalog.Insert("abc @fr def", 4, 7, from);

        Assert.Equal("abc from: def", text);
        Assert.Equal(9, caret);
    }

    [Fact]
    public void 插入_完整语法补空格_光标停在空格后()
    {
        var flag = SearchSyntaxCatalog.All.Single(item => item.Token == "has:attachment");
        var (text, caret) = SearchSyntaxCatalog.Insert("abc @ha", 4, 7, flag);

        Assert.Equal("abc has:attachment ", text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void 插入_已有后续空格时不重复补空格()
    {
        var flag = SearchSyntaxCatalog.All.Single(item => item.Token == "is:unread");
        var (text, caret) = SearchSyntaxCatalog.Insert("abc @un rest", 4, 7, flag);

        Assert.Equal("abc is:unread rest", text);
        Assert.Equal("abc is:unread".Length, caret);
    }

    [Fact]
    public void 主窗口_保留搜索语法弹出层与事件绑定()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml");
        Assert.True(File.Exists(path), "缺少主窗口 XAML：" + path);

        var xaml = File.ReadAllText(path);
        Assert.Contains("x:Name=\"SearchSyntaxPopup\"", xaml);
        Assert.Contains("x:Name=\"SearchSyntaxList\"", xaml);
        Assert.Contains("TextChanged=\"OnSearchBoxTextChanged\"", xaml);
        Assert.Contains("PreviewKeyDown=\"OnSearchBoxPreviewKeyDown\"", xaml);
        Assert.Contains("输入 @ 选择语法", xaml);
    }

    [Fact]
    public void 主窗口_账户行提供单账户同步入口()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml");
        Assert.True(File.Exists(path), "缺少主窗口 XAML：" + path);

        var xaml = File.ReadAllText(path);
        // 账户行右侧的同步按钮 + 右键菜单，两处都绑定 SyncAccountCommand
        Assert.Equal(2, xaml.Split("SyncAccountCommand").Length - 1);
        Assert.Contains("立即同步此账户", xaml);
    }
}
