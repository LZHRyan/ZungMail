using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>运输字段搜索前缀（045）：size / lane / term / price 与既有运算符互不干扰。</summary>
[Trait("Category", "Unit")]
public sealed class MailSearchQueryShipmentTests
{
    [Fact]
    public void 箱型与前缀解析()
    {
        var query = MailSearchQuery.Parse("size:40HC");
        Assert.Equal("40HC", query.ShipmentSize);
        Assert.Equal("", query.FreeText);
    }

    [Fact]
    public void 航线与前缀解析且保留其他条件()
    {
        var query = MailSearchQuery.Parse("lane:ningbo type:si");
        Assert.Equal("ningbo", query.ShipmentLane);
        Assert.Equal(ZungMail.Core.Domain.MailType.ShippingInstruction, query.MailType);
        Assert.Equal("", query.FreeText);
    }

    [Fact]
    public void 条款统一大写()
    {
        var query = MailSearchQuery.Parse("term:fob");
        Assert.Equal("FOB", query.Incoterm);
    }

    [Fact]
    public void 金额与箱号前缀不混淆()
    {
        var priceQuery = MailSearchQuery.Parse("price:USD");
        Assert.Equal("USD", priceQuery.PriceText);
        Assert.Empty(priceQuery.References);

        // ctr: 仍然是箱号（Container），不会被 size: 抢走
        var containerQuery = MailSearchQuery.Parse("ctr:DLRU0087214");
        Assert.Single(containerQuery.References);
        Assert.Null(containerQuery.ShipmentSize);
    }

    [Fact]
    public void 中文运算符也认()
    {
        var query = MailSearchQuery.Parse("箱型:40HQ 航线:上海 条款:CIF 金额:EUR");
        Assert.Equal("40HQ", query.ShipmentSize);
        Assert.Equal("上海", query.ShipmentLane);
        Assert.Equal("CIF", query.Incoterm);
        Assert.Equal("EUR", query.PriceText);
    }
}
