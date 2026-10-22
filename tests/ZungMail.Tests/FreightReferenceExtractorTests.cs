using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>货代业务标识抽取：真实邮件里的单号形态要能识别，普通英文不能误报。</summary>
[Trait("Category", "Unit")]
public sealed class FreightReferenceExtractorTests
{
    private static FreightReference Single(IReadOnlyList<FreightReference> refs, FreightReferenceKind kind)
        => Assert.Single(refs, r => r.Kind == kind);

    [Fact]
    public void 主题中的订舱号_带NO且值含前缀_完整保留()
    {
        var refs = FreightReferenceExtractor.Extract("SO No.: SO-20261017-028 / Booking confirmation", null);

        var booking = Single(refs, FreightReferenceKind.Booking);
        Assert.Equal("SO-20261017-028", booking.Value);
        Assert.Equal("SO20261017028", booking.Normalized);
        Assert.Equal(ReferenceSource.Subject, booking.Source);
        Assert.Equal("booking:SO-20261017-028", booking.SearchText);
    }

    [Fact]
    public void 提单号_支持HBL标签与冒号分隔()
    {
        var refs = FreightReferenceExtractor.Extract("B/L No. HBL: SHAXXX260917", null);

        var bl = Single(refs, FreightReferenceKind.BillOfLading);
        Assert.Equal("SHAXXX260917", bl.Value);
    }

    [Fact]
    public void 箱号_通过ISO6346校验位才收录()
    {
        var valid = FreightReferenceExtractor.Extract("Container No.: TGHU1234567", null);
        var container = Single(valid, FreightReferenceKind.Container);
        Assert.Equal("TGHU1234567", container.Value);

        var invalid = FreightReferenceExtractor.Extract("Container No.: TGHU1234565", null);
        Assert.DoesNotContain(invalid, r => r.Kind == FreightReferenceKind.Container);
    }

    [Fact]
    public void 箱号_正文中的裸单号也能识别()
    {
        var refs = FreightReferenceExtractor.Extract(null, "请安排拖车，柜号 TGHU1234567，谢谢。");

        var container = Single(refs, FreightReferenceKind.Container);
        Assert.Equal(ReferenceSource.Body, container.Source);
    }

    [Fact]
    public void 空运单_校验位正确才收录()
    {
        Assert.True(FreightReferenceExtractor.IsValidAirWaybill("999-12345675"));
        Assert.False(FreightReferenceExtractor.IsValidAirWaybill("999-12345670"));

        var refs = FreightReferenceExtractor.Extract("AWB 999-12345675", null);
        Assert.Equal("99912345675", Single(refs, FreightReferenceKind.AirWaybill).Value);
    }

    [Fact]
    public void 空运单_支持运单3加4加4版面()
    {
        var refs = FreightReferenceExtractor.Extract("AWB 057 1234 5675", null);

        Assert.Equal("05712345675", Single(refs, FreightReferenceKind.AirWaybill).Value);
    }

    [Fact]
    public void 空运单_拒绝URL里的全零数字串()
    {
        var refs = FreightReferenceExtractor.Extract(
            "Update of the EULAs",
            "https://services.example.com/d?o0000dry0l3fdy00d0000pxi00000000000i5looti4yh4on6dilc22jo60");

        Assert.DoesNotContain(refs, r => r.Kind == FreightReferenceKind.AirWaybill);
    }

    [Fact]
    public void 港口_不把Polygon里的Pol当标签()
    {
        var refs = FreightReferenceExtractor.Extract("CustomRoundedCornerPolygon", null);

        Assert.DoesNotContain(refs, r => r.Kind == FreightReferenceKind.Port);
    }

    [Fact]
    public void 中文订单号_不当作采购订单()
    {
        var refs = FreightReferenceExtractor.Extract(
            "京东礼品卡消费通知",
            "订单号：3434426016584713，如有疑问请联系客服");

        Assert.DoesNotContain(refs, r => r.Kind == FreightReferenceKind.PurchaseOrder);
    }

    [Fact]
    public void 带前缀与不带前缀的同一单号_只保留更完整的一条()
    {
        var refs = FreightReferenceExtractor.Extract(
            "Booking Confirmation BKG20261018001",
            "Booking No.: BKG20261018001");

        var booking = Single(refs, FreightReferenceKind.Booking);
        Assert.Equal("BKG20261018001", booking.Value);
    }

    [Fact]
    public void 船名航次_截断掉后面的ETD字段()
    {
        var refs = FreightReferenceExtractor.Extract("Vessel: MSC ISABELLA 123W ETD 2026-10-20", null);

        Assert.Equal("MSC ISABELLA 123W", Single(refs, FreightReferenceKind.VesselVoyage).Value);
    }

    [Fact]
    public void 港口_起运港与目的港分别识别()
    {
        var refs = FreightReferenceExtractor.Extract("POL: Shanghai POD: Los Angeles", null);

        var ports = refs.Where(r => r.Kind == FreightReferenceKind.Port).Select(r => r.Value).ToList();
        Assert.Contains("Shanghai", ports);
        Assert.Contains("Los Angeles", ports);
    }

    [Fact]
    public void 港口_常见UNLOCODE带中文名()
    {
        var refs = FreightReferenceExtractor.Extract("Route CNSHA -> USLAX", null);

        var ports = refs.Where(r => r.Kind == FreightReferenceKind.Port).Select(r => r.Value).ToList();
        Assert.Contains("CNSHA 上海", ports);
        Assert.Contains("USLAX 洛杉矶", ports);
    }

    [Fact]
    public void 中文标签_订舱号与业务编号()
    {
        var refs = FreightReferenceExtractor.Extract("订舱号：SH12345678", "业务编号 JOB-2026-00123");

        Assert.Equal("SH12345678", Single(refs, FreightReferenceKind.Booking).Value);
        Assert.Equal("JOB-2026-00123", Single(refs, FreightReferenceKind.Job).Value);
    }

    [Fact]
    public void 发票与PO_带标签识别且不重复()
    {
        var refs = FreightReferenceExtractor.Extract("Invoice No.: INV-2026-001 / PO No.: 4500123456", null);

        Assert.Equal("INV-2026-001", Single(refs, FreightReferenceKind.Invoice).Value);
        Assert.Equal("4500123456", Single(refs, FreightReferenceKind.PurchaseOrder).Value);
    }

    [Fact]
    public void 普通英文_不产生误报()
    {
        var refs = FreightReferenceExtractor.Extract(
            "Please note so we can proceed. BL draft is attached. case 2026-01",
            "Kindly check the invoice details and the shipping schedule. We will confirm later.");

        Assert.Empty(refs);
    }

    [Fact]
    public void 同一封邮件_主题优先且按业务优先级排序()
    {
        var refs = FreightReferenceExtractor.Extract(
            "SO 12345678",
            "Container No.: TGHU1234567，Seal No.: CN998877");

        Assert.Equal(FreightReferenceKind.Booking, refs[0].Kind);
        Assert.Equal(ReferenceSource.Subject, refs[0].Source);
        Assert.All(refs.Where(r => r.Kind != FreightReferenceKind.Booking), r => Assert.Equal(ReferenceSource.Body, r.Source));
    }

    [Theory]
    [InlineData("TGHU1234567", true)]
    [InlineData("TGHU1234565", false)] // 校验位不匹配
    [InlineData("MSCU1234565", false)] // 校验位不匹配
    [InlineData("TGH1234565", false)]  // 长度不足
    [InlineData("TGHU123456X", false)] // 含非数字
    public void 箱号校验_覆盖长度与校验位(string value, bool expected)
        => Assert.Equal(expected, FreightReferenceExtractor.IsValidContainer(value));

    [Theory]
    [InlineData(FreightReferenceKind.Job, "job:")]
    [InlineData(FreightReferenceKind.Booking, "booking:")]
    [InlineData(FreightReferenceKind.BillOfLading, "bl:")]
    [InlineData(FreightReferenceKind.Container, "ctr:")]
    [InlineData(FreightReferenceKind.Port, "port:")]
    [InlineData(FreightReferenceKind.AirWaybill, "awb:")]
    public void 搜索表达式_使用各类型关键字(FreightReferenceKind kind, string prefix)
    {
        var reference = new FreightReference { Kind = kind, Value = "X1", Normalized = "X1" };

        Assert.StartsWith(prefix, reference.SearchText, StringComparison.OrdinalIgnoreCase);
    }
}
