using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 类型识别的「补召回」规则（044）：多语言强词、主题位次优先级、弱词需业务信号、会话继承。
/// 用例里的主题全部取自本机真实邮件（含俄语往来与多单据并存主题）。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MailTypeRecallTests
{
    private static MailTypeResult Classify(string subject, string? body = null, bool signal = false)
        => MailTypeClassifier.Classify(subject, body, signal);

    [Theory]
    [InlineData("Запрос / ершик / Китай, Гуанчжоу - Россия, СПб / 40HC", "inquiry")]
    [InlineData("Запрос / Светильники / GuangDong - СПб / сборка", "inquiry")]
    [InlineData("Запрос / FOB Тяньцзинь - ДВ - Москва / пены монтажные / 20 DC", "inquiry")]
    public void 俄语询价_按强词识别(string subject, string _)
    {
        Assert.Equal(MailType.Inquiry, Classify(subject).Type);
    }

    [Fact]
    public void 提单样本_是提单草稿的强词()
    {
        var result = Classify("Re: Re: 提单样本//: TS-SH-2610015-2 // 1*40HC FOB SHANGHAI- VRANGEL");
        Assert.Equal(MailType.DraftBillOfLading, result.Type);
        Assert.Contains("提单样本", result.Basis);
        Assert.Contains("命中「提单样本」", result.BasisText);
    }

    [Fact]
    public void 多文档并存_按主题位次判定()
    {
        // 真实主题：「账单」在前、「提单样本」在后 → 判账单（修复「单关键词互抢」）
        var result = Classify("回复：账单// 提单样本//TS-SH-2610085 // 1*20GP FOB SHANGHAI- NAKHODKA");
        Assert.Equal(MailType.DebitNote, result.Type);
        Assert.Contains("账单", result.Basis);
    }

    [Fact]
    public void SI主题里带BOOKING_判补料()
    {
        var result = Classify("SI  NNLTXG261609353 Re: Re: Xingang_DLFF_rel_0156-33515178-1_BOOKING_1X40HC");
        Assert.Equal(MailType.ShippingInstruction, result.Type);
    }

    [Fact]
    public void 异常类永远优先()
    {
        var result = Classify("甩柜通知 // 提单样本 // TS-NB-2610011 // 1*40HC");
        Assert.Equal(MailType.Rollover, result.Type);
        Assert.True(result.IsException);
    }

    [Fact]
    public void 弱词必须有业务信号()
    {
        // 银行电子账单：有「对账单」但没有单号 / 箱型 → 不判货代账单
        Assert.Equal(MailType.Unknown, Classify("中国农业银行金穗信用卡电子对账单").Type);

        // 同样的词，带箱型箱量（业务信号）→ 判账单
        Assert.Equal(MailType.DebitNote, Classify("对账单 // 1*40HC FOB NINGBO").Type);
    }

    [Fact]
    public void 弱词在有单号时生效()
    {
        var result = Classify("Booking update", "please confirm", signal: true);
        Assert.Equal(MailType.BookingConfirmation, result.Type);
        Assert.Contains("booking", result.Basis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 正文多个强词同类型_仍然判定()
    {
        var result = Classify("更新", "Please see the shipping instruction and si cut-off below.");
        Assert.Equal(MailType.ShippingInstruction, result.Type);
        Assert.Contains("@body", result.Basis);
    }

    [Fact]
    public void 正文出现两类强词_不猜()
    {
        // 长引用里同时出现提单与账单：宁可不判，也不乱贴类型
        var result = Classify("更新", "提单样本如下，另附 debit note 供核对。");
        Assert.Equal(MailType.Unknown, result.Type);
    }

    [Fact]
    public void 单号里的MBL_不算提单()
    {
        // 弱词 bl 必须整词命中：MBL / HBL 是主单/分单号，不是「提单」这个单据名
        var result = Classify("Re: 1030919  MBL: TS-SH-2610015    1*40HC FOB SHANGHAI- VRANGEL");
        Assert.NotEqual(MailType.DraftBillOfLading, result.Type);
    }

    [Fact]
    public void 转发里的旧单据_不影响当前邮件()
    {
        // 正文出现转发头（From:/发件人:）时，正文强词不参与判定
        const string body = "Please check the attached EIR.\nFrom: ops\nSubject: 提单样本 // TS-SH-2610015";
        Assert.Equal(MailType.Unknown, Classify("更新", body).Type);
    }

    [Fact]
    public void 依据文案可读()
    {
        Assert.Equal("命中「提单样本」（主题第 9 字）", MailTypeClassifier.DescribeBasis("key:提单样本@9"));
        Assert.Equal("命中「booking」（正文）", MailTypeClassifier.DescribeBasis("key:booking@body"));
        Assert.Equal("继承会话类型", MailTypeClassifier.DescribeBasis(MailTypeClassifier.ThreadBasis));
        Assert.Equal("", MailTypeClassifier.DescribeBasis(""));
    }

    [Fact]
    public void 会话继承_异常优先其次多数()
    {
        var winner = MailTypeThreadInheritance.PickWinner(new Dictionary<MailType, int>
        {
            [MailType.DebitNote] = 5,
            [MailType.ShippingInstruction] = 3,
            [MailType.Rollover] = 1
        });
        Assert.Equal(MailType.Rollover, winner);

        var majority = MailTypeThreadInheritance.PickWinner(new Dictionary<MailType, int>
        {
            [MailType.DebitNote] = 2,
            [MailType.ShippingInstruction] = 4
        });
        Assert.Equal(MailType.ShippingInstruction, majority);

        Assert.Equal(MailType.Unknown, MailTypeThreadInheritance.PickWinner(new Dictionary<MailType, int>()));
    }
}
