using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 正文文档的组装缓存：小正文就地组装、大正文后台组装，同一封邮件只组装一次。
/// 这是「打开 8MB 邮件时界面线程被占 200–300ms」的修复核心，两个 ViewModel 共用。
/// </summary>
public sealed class MailBodyDocumentCacheTests
{
    private static EmailDetail Detail(string html = "<p>Dear Anna,</p>", string text = "Dear Anna,")
        => new()
        {
            Id = 42,
            BodyHtml = html,
            BodyText = text,
            BodyState = BodyState.Downloaded
        };

    private static MailDocumentOptions Options()
        => MailBodyDocumentFactory.CreateOptions(
            isDark: false, fontSize: 13.5, allowRemoteImages: false,
            foldQuotedHistory: true, foldQuotedHistoryKeepLayers: 5, emlDirectory: null);

    [Fact]
    public void 小正文就地组装并缓存()
    {
        var detail = Detail();
        var options = Options();
        var cache = new MailBodyDocumentCache();
        var key = MailBodyDocumentFactory.BuildKey(detail, options);
        var calls = 0;

        Assert.True(cache.TryBuildInline(key, () => { calls++; return MailBodyDocumentFactory.Build(detail, options); }));
        Assert.Equal(1, calls);
        Assert.False(string.IsNullOrEmpty(cache.Document));

        // 同一个 key 再求值：命中缓存，不重复组装（绑定会反复读 getter）
        Assert.False(cache.TryBuildInline(key, () => { calls++; return MailBodyDocumentFactory.Build(detail, options); }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void 正文或选项变化后重新组装()
    {
        var options = Options();
        var cache = new MailBodyDocumentCache();
        var first = Detail();
        var firstKey = MailBodyDocumentFactory.BuildKey(first, options);
        Assert.True(cache.TryBuildInline(firstKey, () => MailBodyDocumentFactory.Build(first, options)));

        // 正文重新下载：长度变了 → 键变了
        var reloaded = Detail(html: "<p>Dear Anna, hello again</p>");
        var reloadedKey = MailBodyDocumentFactory.BuildKey(reloaded, options);
        Assert.NotEqual(firstKey, reloadedKey);
        Assert.True(cache.TryBuildInline(reloadedKey, () => MailBodyDocumentFactory.Build(reloaded, options)));

        // 主题切换：暗色是另一份文档
        var dark = MailBodyDocumentFactory.CreateOptions(
            isDark: true, fontSize: 13.5, allowRemoteImages: false,
            foldQuotedHistory: true, foldQuotedHistoryKeepLayers: 5, emlDirectory: null);
        Assert.NotEqual(reloadedKey, MailBodyDocumentFactory.BuildKey(reloaded, dark));
    }

    [Fact]
    public async Task 大正文后台组装后用于绑定()
    {
        var detail = Detail();
        var options = Options();
        var cache = new MailBodyDocumentCache();
        var key = MailBodyDocumentFactory.BuildKey(detail, options);

        Assert.True(await cache.BuildAsync(key, () => MailBodyDocumentFactory.Build(detail, options)));
        Assert.Contains("Dear Anna", cache.Document);
    }

    [Fact]
    public async Task 过期的后台组装不覆盖新结果()
    {
        var options = Options();
        var cache = new MailBodyDocumentCache();
        var older = Detail(html: "<p>older</p>");
        var newer = Detail(html: "<p>newer</p>");
        var olderKey = MailBodyDocumentFactory.BuildKey(older, options);
        var newerKey = MailBodyDocumentFactory.BuildKey(newer, options);

        // 先发一个慢请求，再发新请求：慢的结果必须被丢弃
        var slow = cache.BuildAsync(olderKey, () =>
        {
            Thread.Sleep(120);
            return MailBodyDocumentFactory.Build(older, options);
        });
        Assert.True(await cache.BuildAsync(newerKey, () => MailBodyDocumentFactory.Build(newer, options)));
        Assert.False(await slow);

        Assert.Contains("newer", cache.Document);
        Assert.DoesNotContain("older", cache.Document);
    }

    [Fact]
    public void 清空后回到空文档()
    {
        var detail = Detail();
        var options = Options();
        var cache = new MailBodyDocumentCache();
        var key = MailBodyDocumentFactory.BuildKey(detail, options);
        Assert.True(cache.TryBuildInline(key, () => MailBodyDocumentFactory.Build(detail, options)));

        cache.Reset();
        Assert.Equal(string.Empty, cache.Document);
        Assert.False(cache.HasRemoteImages);

        // 清空后同一个 key 也要重新组装
        Assert.True(cache.TryBuildInline(key, () => MailBodyDocumentFactory.Build(detail, options)));
    }

    [Fact]
    public void 空正文给占位文档()
    {
        var detail = Detail(html: "", text: "");
        var options = Options();
        var built = MailBodyDocumentFactory.Build(detail, options);

        Assert.Contains("mail-empty", built.Html);
    }

    [Fact]
    public void 远程图片标记随文档一起算出()
    {
        var options = Options();
        var withRemote = Detail(html: "<p><img src=\"https://example.com/a.png\"></p>");
        var local = Detail(html: "<p><img src=\"data:image/png;base64,AAAA\"></p>");

        Assert.True(MailBodyDocumentFactory.Build(withRemote, options).HasRemoteImages);
        Assert.False(MailBodyDocumentFactory.Build(local, options).HasRemoteImages);
    }

    [Fact]
    public void 没有邮件时缓存键为空()
        => Assert.Equal(string.Empty, MailBodyDocumentFactory.BuildKey(null, Options()));
}
