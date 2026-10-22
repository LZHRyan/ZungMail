using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 时效节点抽取：截关 / 截补料 / VGM / 单证 / CY / ETD / ETA。
/// 核心约束是「只有关键词没有时间不产生节点」，否则看板会被噪声刷满。
/// </summary>
[Trait("Category", "Unit")]
public sealed class FreightDeadlineExtractorTests
{
    private static readonly DateTimeOffset Reference = new(2026, 3, 1, 9, 0, 0, TimeSpan.FromHours(8));

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
        => new DateTimeOffset(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified))
            .ToUniversalTime();

    private static FreightDeadline Single(IReadOnlyList<FreightDeadline> items, FreightDeadlineKind kind)
        => Assert.Single(items, d => d.Kind == kind);

    [Fact]
    public void 中文截关_带冒号与完整时间()
    {
        var items = FreightDeadlineExtractor.Extract("SO SHA1234567", "截关时间：2026-03-15 12:00，请提前送货。", Reference);

        var deadline = Single(items, FreightDeadlineKind.CutOff);
        Assert.Equal(Local(2026, 3, 15, 12, 0), deadline.DueUtc);
        Assert.Equal(ReferenceSource.Body, deadline.Source);
        Assert.Contains("截关", deadline.RawText);
    }

    [Fact]
    public void 截补料_中文月日加时间()
    {
        var items = FreightDeadlineExtractor.Extract("SI CUT-OFF: 3月12日 17:00", null, Reference);

        var deadline = Single(items, FreightDeadlineKind.SiCutOff);
        Assert.Equal(Local(2026, 3, 12, 17, 0), deadline.DueUtc);
        // 「SI CUT-OFF」不应同时被算成通用截关
        Assert.DoesNotContain(items, d => d.Kind == FreightDeadlineKind.CutOff);
    }

    [Fact]
    public void VGM截关_没有时间按当日2359()
    {
        var items = FreightDeadlineExtractor.Extract(null, "VGM CUTOFF 2026-03-10", Reference);

        var deadline = Single(items, FreightDeadlineKind.VgmCutOff);
        Assert.Equal(Local(2026, 3, 10, 23, 59), deadline.DueUtc);
    }

    [Fact]
    public void CY截关_优先识别为CY而不是通用截关()
    {
        var items = FreightDeadlineExtractor.Extract("CY CUT OFF 2026-03-09 18:00", null, Reference);

        Assert.Single(items);
        Assert.Equal(FreightDeadlineKind.CyCutOff, items[0].Kind);
        Assert.Equal(Local(2026, 3, 9, 18, 0), items[0].DueUtc);
    }

    [Fact]
    public void 截单证_识别DOC标签()
    {
        var items = FreightDeadlineExtractor.Extract(null, "DOC CUT-OFF: 2026/03/11 10:30", Reference);

        var deadline = Single(items, FreightDeadlineKind.DocCutOff);
        Assert.Equal(Local(2026, 3, 11, 10, 30), deadline.DueUtc);
    }

    [Fact]
    public void ETD_英文月份与斜杠日期()
    {
        var monthName = FreightDeadlineExtractor.Extract("ETD: 15 MAR 2026", null, Reference);
        Assert.Equal(Local(2026, 3, 15, 23, 59), Single(monthName, FreightDeadlineKind.Etd).DueUtc);

        var slash = FreightDeadlineExtractor.Extract("ETD 5/6/26", null, Reference);
        Assert.Equal(Local(2026, 5, 6, 23, 59), Single(slash, FreightDeadlineKind.Etd).DueUtc);
    }

    [Fact]
    public void ETA_ISO日期与时间()
    {
        var items = FreightDeadlineExtractor.Extract("Arrival Notice ETA 2026-03-18 08:00", null, Reference);

        var deadline = Single(items, FreightDeadlineKind.Eta);
        Assert.Equal(Local(2026, 3, 18, 8, 0), deadline.DueUtc);
        Assert.Equal("到港", deadline.KindLabel);
    }

    [Fact]
    public void 只有关键词没有日期_不产生节点()
    {
        var items = FreightDeadlineExtractor.Extract("截关时间请见附件", "VGM CUTOFF 请提前提交，谢谢。", Reference);

        Assert.Empty(items);
    }

    [Fact]
    public void 非法日期_不产生节点()
    {
        var items = FreightDeadlineExtractor.Extract("ETA 2026/13/45", "截关 2月30日", Reference);

        Assert.Empty(items);
    }

    [Fact]
    public void 无年份日期_跨年时归到下一年()
    {
        var reference = new DateTimeOffset(2026, 12, 20, 10, 0, 0, TimeSpan.FromHours(8));
        var items = FreightDeadlineExtractor.Extract("ETD 5 JAN", null, reference);

        Assert.Equal(Local(2027, 1, 5, 23, 59), Single(items, FreightDeadlineKind.Etd).DueUtc);
    }

    [Fact]
    public void 全文大写噪声_不误报()
    {
        // 调研文档附录 B 里的真实误报样本：DOCS CUT OFF 2024 曾被当成有效节点
        var items = FreightDeadlineExtractor.Extract(
            null,
            "PLEASE CONFIRM RECEIPT. DOCS CUT OFF 2024. QUOTATION VALID FOR 30 DAYS.",
            Reference);

        Assert.Empty(items);
    }

    [Fact]
    public void 营销邮件里的通用截止日期_不算截关()
    {
        // 真实邮箱数据里实测到的误报：华为服务公告「截止：2026年6月8日 18:00」
        var items = FreightDeadlineExtractor.Extract(
            "【服务公告】华为审核专家1v1深度交流预约开启",
            "报名截止：2026年6月8日 18:00，请尽快预约。",
            Reference);

        Assert.Empty(items);
    }

    [Fact]
    public void 同一段多个节点_各自独立并按时序排列()
    {
        var body = "CY CUT OFF: 2026-03-09 18:00\nSI CUT-OFF: 2026-03-08 12:00\nETD 2026-03-12";
        var items = FreightDeadlineExtractor.Extract("SO SHA1234567", body, Reference);

        Assert.Equal(3, items.Count);
        Assert.Equal(FreightDeadlineKind.SiCutOff, items[0].Kind);
        Assert.Equal(FreightDeadlineKind.CyCutOff, items[1].Kind);
        Assert.Equal(FreightDeadlineKind.Etd, items[2].Kind);
    }

    [Fact]
    public void 倒计时文案_区分未到期与超时()
    {
        var due = Reference.AddHours(4).AddMinutes(30);
        var upcoming = new FreightDeadline { Kind = FreightDeadlineKind.CutOff, DueUtc = due };
        Assert.Equal("距截关 4 小时", upcoming.Countdown(Reference));
        Assert.Equal(DeadlineUrgency.Critical, upcoming.Urgency(Reference));

        var overdue = new FreightDeadline { Kind = FreightDeadlineKind.SiCutOff, DueUtc = Reference.AddHours(-2) };
        Assert.Equal("已超时 2 小时", overdue.Countdown(Reference));
        Assert.Equal(DeadlineUrgency.Overdue, overdue.Urgency(Reference));
    }

    [Fact]
    public void 邮件时间靠后时_关键字后的时间跨行也能识别()
    {
        var body = "截关时间：\n2026-03-15 12:00";
        var items = FreightDeadlineExtractor.Extract(null, body, Reference);

        Assert.Equal(Local(2026, 3, 15, 12, 0), Single(items, FreightDeadlineKind.CutOff).DueUtc);
    }
}
