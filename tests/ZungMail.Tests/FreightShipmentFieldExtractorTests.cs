using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 运输字段抽取（045）：箱型箱量、航线对、贸易条款、金额。
/// 用例取自本机真实邮件，反例集来自实测误抽（文件名当航线、箱型残渣当港口、银行账单金额）。
/// </summary>
[Trait("Category", "Unit")]
public sealed class FreightShipmentFieldExtractorTests
{
    [Fact]
    public void 箱型箱量_常见写法()
    {
        var boxes = FreightShipmentFieldExtractor.ExtractBoxes(
            "1031385 MBL+HBL TS-SH-2610076 FOB 1*40HC NINGBO-VRANGEL");
        Assert.Single(boxes);
        Assert.Equal("1×40HC", boxes[0].Display);
    }

    [Fact]
    public void 箱型箱量_多箱型混装()
    {
        var boxes = FreightShipmentFieldExtractor.ExtractBoxes("RULN35064 RTG-33535 3x40HC + 1x20DC SOC voy 561");
        Assert.Equal(2, boxes.Count);
        Assert.Equal("3×40HC", boxes[0].Display);
        Assert.Equal("1×20DC", boxes[1].Display);
    }

    [Fact]
    public void 箱型箱量_别名归一与去重()
    {
        var boxes = FreightShipmentFieldExtractor.ExtractBoxes("2*40HQ 与 2 x 40hc，再来一个 1*20'DV");
        Assert.Contains(boxes, box => box.Display == "2×40HC");
        Assert.Contains(boxes, box => box.Display == "1×20DC");
    }

    [Fact]
    public void 箱型箱量_没有类型不输出()
    {
        Assert.Empty(FreightShipmentFieldExtractor.ExtractBoxes("1*40 的箱子请安排"));
    }

    [Fact]
    public void 航线_标准写法()
    {
        var lane = FreightShipmentFieldExtractor.FindLane(
            "Re: 1030919 MBL TS-SH-2610015 1*40HC FOB SHANGHAI- VRANGEL");
        Assert.NotNull(lane);
        Assert.Equal("上海", lane!.From);
        Assert.Equal("符拉迪沃斯托克", lane.To);
    }

    [Fact]
    public void 航线_俄语写法()
    {
        var lane = FreightShipmentFieldExtractor.FindLane("Запрос / ершик / Китай, Гуанчжоу - Россия, СПб / 40HC");
        Assert.NotNull(lane);
        Assert.Equal("广州", lane!.From);
        Assert.Equal("圣彼得堡", lane.To);
    }

    [Fact]
    public void 航线_文件名与问候语不算()
    {
        Assert.Null(FreightShipmentFieldExtractor.FindLane("LogoTLC.png -> Best regards"));
        Assert.Null(FreightShipmentFieldExtractor.FindLane("Best regards, Anna"));
    }

    [Fact]
    public void 航线_词表外的词不当港口()
    {
        // PRAMO 不在词表里，不能和 XINGANG 组成航线
        Assert.Null(FreightShipmentFieldExtractor.FindLane("PRAMO -> XINGANG NAKHODKA"));
    }

    [Fact]
    public void 贸易条款_主题优先且只认大写()
    {
        Assert.Equal("FOB", FreightShipmentFieldExtractor.FindIncoterm("1030954 / MBL // TS-NB-2610011 // FOB 1*40HC"));
        Assert.Equal("", FreightShipmentFieldExtractor.FindIncoterm("please fob it over"));
    }

    [Fact]
    public void 金额_运费语境命中()
    {
        var (amount, currency) = FreightShipmentFieldExtractor.FindAmount(
            "报价更新", "海运费 300 USD per 40HC，含本地费用。");
        Assert.Equal(300m, amount);
        Assert.Equal("USD", currency);
    }

    [Fact]
    public void 金额_被换行拆开也能解析()
    {
        var (amount, currency) = FreightShipmentFieldExtractor.FindAmount(
            "报价", "运费 28\nEur per cbm");
        Assert.Equal(28m, amount);
        Assert.Equal("EUR", currency);
    }

    [Fact]
    public void 金额_银行账单不算运费()
    {
        var (amount, _) = FreightShipmentFieldExtractor.FindAmount(
            "中信银行信用卡电子账单", "本期应还 CNY 2167.37，请按时还款。");
        Assert.Null(amount);
    }

    [Fact]
    public void 金额_没有运费语境不抽()
    {
        var (amount, _) = FreightShipmentFieldExtractor.FindAmount("装箱单", "共 300 USD 报关值，件数 12 箱。");
        Assert.Null(amount);
    }

    [Fact]
    public void 整封抽取_组合字段()
    {
        var fields = FreightShipmentFieldExtractor.Extract(
            "1030954 / MBL // TS-NB-2610011 // FOB 1*40HC  NINGBO-VRANGEL",
            "海运费 900 USD，截关 10.23。");
        Assert.True(fields.HasAny);
        Assert.Equal("1×40HC", fields.BoxesText);
        Assert.NotNull(fields.Lane);
        Assert.Equal("FOB", fields.Incoterm);
        Assert.Equal("900 USD", fields.AmountDisplay);
    }
}
