using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 行业邮件类型识别：船司 / 代理的模板化来信要能稳定命中，
/// 普通邮件与只在页脚出现 invoice 的邮件不能误判。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MailTypeClassifierTests
{
    private static MailType TypeOf(string subject, string? body = null)
        => MailTypeClassifier.Classify(subject, body).Type;

    [Fact]
    public void 订舱确认_SO_Release()
    {
        var result = MailTypeClassifier.Classify("Booking Confirmation SO No.: SHA1234567", "Dear Ops, booking confirmed.");
        Assert.Equal(MailType.BookingConfirmation, result.Type);
        Assert.True(result.Score >= 3);
        Assert.False(result.IsException);
    }

    [Fact]
    public void 补料_SI()
    {
        Assert.Equal(MailType.ShippingInstruction, TypeOf("SI CUT-OFF: 3月12日 17:00"));
        Assert.Equal(MailType.ShippingInstruction, TypeOf("RE: Shipping Instruction for SO SHA1234567"));
    }

    [Fact]
    public void VGM_提单草稿_到港通知_账单()
    {
        Assert.Equal(MailType.Vgm, TypeOf("VGM submission - SO SHA1234567"));
        Assert.Equal(MailType.DraftBillOfLading, TypeOf("Draft B/L for your confirmation"));
        Assert.Equal(MailType.ArrivalNotice, TypeOf("Arrival Notice - MSC ISABELLA V.234E / ETA 18 Mar"));
        Assert.Equal(MailType.DebitNote, TypeOf("Debit Note from XX Shipping", "Please settle within 7 days."));
    }

    [Fact]
    public void 异常类型_甩柜与查验()
    {
        var rollover = MailTypeClassifier.Classify("甩柜通知：SO SHA1234567 需改配下一班", null);
        Assert.Equal(MailType.Rollover, rollover.Type);
        Assert.True(rollover.IsException);

        var inspection = MailTypeClassifier.Classify("海关查验通知", "请配合开箱查验。");
        Assert.Equal(MailType.CustomsInspection, inspection.Type);
        Assert.True(inspection.IsException);
    }

    [Fact]
    public void 询价与报价()
    {
        Assert.Equal(MailType.Inquiry, TypeOf("RFQ Shanghai to Los Angeles 1x40HQ"));
        Assert.Equal(MailType.Quotation, TypeOf("Quotation SHA-LAX 40HQ valid to 30 Apr"));
    }

    [Fact]
    public void 普通邮件_不误判()
    {
        Assert.Equal(MailType.Unknown, TypeOf("“Foxmail”已获得了你的QQ邮箱账号的访问权限"));
        Assert.Equal(MailType.Unknown, TypeOf("【服务公告】华为审核专家1v1深度交流预约开启"));
        Assert.Equal(MailType.Unknown, TypeOf(""));
    }

    [Fact]
    public void 只含通用账单词_不算货代账单()
    {
        // 真实邮箱数据里实测到的误报：银行信用卡「电子账单 / 电子对账单」
        Assert.Equal(MailType.Unknown, TypeOf("平安信用卡电子账单"));
        Assert.Equal(MailType.Unknown, TypeOf("中国农业银行金穗信用卡电子对账单"));

        // 货代账单仍然要命中
        Assert.Equal(MailType.DebitNote, TypeOf("Debit Note - 港杂费结算"));
        Assert.Equal(MailType.DebitNote, TypeOf("Freight invoice no. SZ/2024/0001"));
    }

    [Fact]
    public void 只在页脚出现invoice_不算账单()
    {
        // 正文单关键词只加 1 分，低于阈值
        var result = MailTypeClassifier.Classify("周报", "本周进度如下，附 invoice 模板供参考。");
        Assert.Equal(MailType.Unknown, result.Type);
    }

    [Fact]
    public void Re前缀_不影响主题命中()
    {
        Assert.Equal(MailType.BookingConfirmation, TypeOf("Re: Fw: Booking Confirmation SO NO. SHA1234567"));
    }

    [Fact]
    public void 正文多个关键词_也能识别()
    {
        var result = MailTypeClassifier.Classify("更新", "Please see the shipping instruction and si cut-off below.");
        Assert.Equal(MailType.ShippingInstruction, result.Type);
    }
}
