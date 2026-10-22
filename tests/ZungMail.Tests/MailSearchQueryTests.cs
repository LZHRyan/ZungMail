using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>结构化搜索表达式解析：运算符与自由文本要能正确分开，未知运算符不能吞掉关键词。</summary>
[Trait("Category", "Unit")]
public sealed class MailSearchQueryTests
{
    [Fact]
    public void 业务标识运算符_转成规范化过滤值并保留自由文本()
    {
        var query = MailSearchQuery.Parse("booking:SO-2026-001 船司 回复");

        var filter = Assert.Single(query.References);
        Assert.Equal(FreightReferenceKind.Booking, filter.Kind);
        Assert.Equal("SO2026001", filter.Value);
        Assert.Equal("船司 回复", query.FreeText);
        Assert.True(query.HasFilters);
    }

    [Theory]
    [InlineData("so:SO123")]
    [InlineData("s/o:SO123")]
    [InlineData("bkg:SO123")]
    [InlineData("订舱号:SO123")]
    public void 订舱号_支持多种别名(string text)
    {
        var query = MailSearchQuery.Parse(text);

        var filter = Assert.Single(query.References);
        Assert.Equal(FreightReferenceKind.Booking, filter.Kind);
        Assert.Equal("SO123", filter.Value);
        Assert.Equal(string.Empty, query.FreeText);
    }

    [Fact]
    public void 任意类型_ref运算符不限定Kind()
    {
        var query = MailSearchQuery.Parse("ref:TGHU1234565");

        var filter = Assert.Single(query.References);
        Assert.Null(filter.Kind);
        Assert.Equal("TGHU1234565", filter.Value);
    }

    [Fact]
    public void 中文箱号运算符()
    {
        var query = MailSearchQuery.Parse("箱号:TGHU1234565");

        Assert.Equal(FreightReferenceKind.Container, Assert.Single(query.References).Kind);
    }

    [Fact]
    public void 状态与附件运算符()
    {
        var query = MailSearchQuery.Parse("has:attachment is:unread 报价");

        Assert.True(query.HasAttachment);
        Assert.True(query.IsUnread);
        Assert.Equal("报价", query.FreeText);
    }

    [Fact]
    public void 已读与星标运算符()
    {
        var read = MailSearchQuery.Parse("is:read");
        Assert.False(read.IsUnread);

        var starred = MailSearchQuery.Parse("is:starred");
        Assert.True(starred.IsStarred);
    }

    [Fact]
    public void 发件人与主题运算符()
    {
        var query = MailSearchQuery.Parse("from:customer@example.com subject:报价 40HQ");

        Assert.Equal("customer@example.com", query.From);
        Assert.Equal("报价", query.Subject);
        Assert.Equal("40HQ", query.FreeText);
    }

    [Fact]
    public void 引号值_保留空格()
    {
        var query = MailSearchQuery.Parse("subject:\"BL draft\" vessel");

        Assert.Equal("BL draft", query.Subject);
        Assert.Equal("vessel", query.FreeText);
    }

    [Fact]
    public void 附件名运算符()
    {
        var query = MailSearchQuery.Parse("filename:提单");

        Assert.Equal("提单", query.FileName);
    }

    [Fact]
    public void 未知运算符_留在自由文本里()
    {
        var query = MailSearchQuery.Parse("http://example.com/page 截图");

        Assert.Equal("http://example.com/page 截图", query.FreeText);
        Assert.False(query.HasFilters);
    }

    [Fact]
    public void 非法has值_不当作运算符()
    {
        var query = MailSearchQuery.Parse("has:image");

        Assert.False(query.HasAttachment);
        Assert.Equal("has:image", query.FreeText);
    }

    [Fact]
    public void 空查询_无任何条件()
    {
        var query = MailSearchQuery.Parse("   ");

        Assert.True(query.IsEmpty);
        Assert.Empty(query.References);
    }
}
