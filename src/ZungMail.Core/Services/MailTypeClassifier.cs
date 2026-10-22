using System.Text.RegularExpressions;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>
/// 货代邮件类型识别：用中英双语关键词给主题 / 正文打分，识别 SO Release、SI、VGM、
/// Draft B/L、Arrival Notice、Debit Note 以及查验 / 甩柜 / 延误等异常邮件。
/// <para>
/// 打分规则：主题命中一个关键词 +3，正文命中 +1，总分 ≥2 才落到某个类型。
/// 这样「正文页脚出现 invoice」不会被当成账单，而「主题写 SI CUT-OFF」能稳定命中。
/// 同分时异常类型优先（甩柜 / 查验 / 延误必须先被看见）。
/// </para>
/// </summary>
public static class MailTypeClassifier
{
    private const int SubjectWeight = 3;
    private const int BodyWeight = 1;
    private const int MinimumScore = 2;
    private const int MaxScore = 9;

    private sealed record Rule(MailType Type, string[] Keywords);

    /// <summary>
    /// 关键词表：只收「货代语境下几乎不会误伤」的短语，避免把普通邮件刷成业务邮件。
    /// 注意「账单 / 对账单」这类通用词不能单独使用——银行信用卡电子账单会全部命中。
    /// </summary>
    private static readonly Rule[] Rules =
    {
        new(MailType.CustomsInspection, new[]
        {
            "customs inspection", "inspection notice", "查验通知", "海关查验", "查柜", "开箱查验"
        }),

        new(MailType.Rollover, new[]
        {
            "rollover", "roll over", "rolled to", "cut and run", "甩柜", "改配"
        }),

        new(MailType.Delay, new[]
        {
            "vessel delay", "delay notice", "port omission", "omit port", "延误通知", "跳港", "晚开", "晚到"
        }),

        new(MailType.BookingConfirmation, new[]
        {
            "booking confirmation", "booking confirmed", "so no", "so release", "shipping order",
            "space confirmed", "订舱确认", "放舱", "舱位确认"
        }),

        new(MailType.DraftBillOfLading, new[]
        {
            "draft b/l", "draft bl", "draft bill of lading", "bl draft", "b/l draft",
            "提单草稿", "提单确认", "提单核对"
        }),

        new(MailType.ShippingInstruction, new[]
        {
            "shipping instruction", "si cut-off", "si cut off", "si cut", "补料", "补料通知"
        }),

        new(MailType.Vgm, new[] { "vgm" }),

        new(MailType.ArrivalNotice, new[]
        {
            "arrival notice", "arrival notification", "到港通知", "提货通知", "到港安排"
        }),

        new(MailType.DebitNote, new[]
        {
            "debit note", "statement of account", "freight invoice", "invoice no",
            "运费结算", "港杂费", "本地费用", "应收账单", "应付账单", "费用确认"
        }),

        new(MailType.BookingRequest, new[]
        {
            "booking request", "booking form", "please book", "订舱委托", "托书", "请订舱"
        }),

        new(MailType.Quotation, new[]
        {
            "quotation", "quote for", "rate offer", "our offer", "报价单", "报价如下", "报盘"
        }),

        new(MailType.Inquiry, new[]
        {
            "rfq", "rate request", "rate inquiry", "询价", "请报价", "报价请求"
        })
    };

    private static readonly Regex PrefixRegex = new(
        @"^\s*(?:(?:re|fw|fwd)\s*[:：]|回复\s*[:：]|转发\s*[:：])\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>识别一封邮件的类型；无法判断时返回 <see cref="MailType.Unknown"/>。</summary>
    public static MailTypeResult Classify(string? subject, string? body)
    {
        var subjectText = Normalize(subject);
        var bodyText = Normalize(body);
        if (subjectText.Length == 0 && bodyText.Length == 0) return MailTypeResult.Unknown;

        MailType bestType = MailType.Unknown;
        var bestScore = 0;
        var bestIsException = false;

        foreach (var rule in Rules)
        {
            var score = 0;
            foreach (var keyword in rule.Keywords)
            {
                if (subjectText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    score += SubjectWeight;
                }
                else if (bodyText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    score += BodyWeight;
                }
            }

            if (score == 0) continue;

            var isException = MailTypes.IsException(rule.Type);
            if (score > bestScore
                || (score == bestScore && isException && !bestIsException))
            {
                bestType = rule.Type;
                bestScore = score;
                bestIsException = isException;
            }
        }

        if (bestScore < MinimumScore) return MailTypeResult.Unknown;
        return new MailTypeResult(bestType, Math.Min(bestScore, MaxScore));
    }

    /// <summary>去掉 Re/Fw/回复/转发 前缀并压缩空白，让主题关键词不被前缀打断。</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var value = text;
        for (var i = 0; i < 3; i++)
        {
            var next = PrefixRegex.Replace(value, string.Empty, 1);
            if (next.Length == value.Length) break;
            value = next;
        }

        return Regex.Replace(value, @"\s+", " ").Trim();
    }
}
