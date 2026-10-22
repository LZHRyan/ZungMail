using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 高级筛选条的输入整理：用户习惯从邮件头/正文里直接复制「姓名 &lt;地址&gt;」、带引号或换行的片段，
/// 这里保证拼进 from: / subject:"…" / filename:"…" 之前都是合法取值。
/// </summary>
public sealed class SearchFilterNormalizerTests
{
    [Theory]
    [InlineData("王小明 <liang@abc.com>", "liang@abc.com")]
    [InlineData("  \"liang@abc.com\"  ", "liang@abc.com")]
    [InlineData("abc@qq.com (工作邮箱)", "abc@qq.com")]
    [InlineData("Leo Liang <leo.liang@example.com>", "leo.liang@example.com")]
    public void 发件人_从复制内容里取出地址(string raw, string expected)
    {
        Assert.Equal(expected, SearchFilterNormalizer.From(raw));
    }

    [Fact]
    public void 发件人_只有尖括号地址时直接取地址()
    {
        Assert.Equal("ops@forwarder.cn", SearchFilterNormalizer.From("<ops@forwarder.cn>"));
    }

    [Theory]
    [InlineData("abc.com", "abc.com")]
    [InlineData("@qq.com", "@qq.com")]
    [InlineData("深圳市示例国际物流", "深圳市示例国际物流")]
    public void 发件人_允许只填域名或中文名(string raw, string expected)
    {
        Assert.Equal(expected, SearchFilterNormalizer.From(raw));
    }

    [Fact]
    public void 发件人_空值返回空串()
    {
        Assert.Equal(string.Empty, SearchFilterNormalizer.From(null));
        Assert.Equal(string.Empty, SearchFilterNormalizer.From("   "));
    }

    [Fact]
    public void 关键词_去掉会截断语法的双引号并压缩换行()
    {
        Assert.Equal("订舱确认 SO123", SearchFilterNormalizer.Keyword(" \"订舱确认\"\r\n  SO123 "));
    }

    [Fact]
    public void 含空白的取值拼查询串时要加引号()
    {
        Assert.Equal("\"Sea Air Logistics\"", SearchFilterNormalizer.QuoteIfNeeded("Sea Air Logistics"));
        Assert.Equal("深圳市示例国际物流", SearchFilterNormalizer.QuoteIfNeeded(" 深圳市示例国际物流 "));
        Assert.Equal("abc.com", SearchFilterNormalizer.QuoteIfNeeded(" abc.com "));
        Assert.Equal(string.Empty, SearchFilterNormalizer.QuoteIfNeeded(""));
    }

    [Fact]
    public void 整理后的条件能被搜索语法解析回来()
    {
        var from = SearchFilterNormalizer.From("王小明 <liang@abc.com>");
        var subject = SearchFilterNormalizer.Keyword("\"订舱 确认\"");
        var query = MailSearchQuery.Parse($"from:{SearchFilterNormalizer.QuoteIfNeeded(from)} subject:\"{subject}\"");

        Assert.Equal("liang@abc.com", query.From);
        Assert.Equal("订舱 确认", query.Subject);
    }
}
