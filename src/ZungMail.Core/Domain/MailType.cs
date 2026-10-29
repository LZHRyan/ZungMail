namespace ZungMail.Core.Domain;

/// <summary>
/// 货代行业邮件类型。船司与代理的来信高度模板化（SO Release、SI、VGM、Draft B/L、AN），
/// 识别出来才能「按类型路由 + 定优先级」，而不是和客户询价混在一个收件箱里。
/// </summary>
public enum MailType
{
    /// <summary>未识别（普通邮件）。</summary>
    Unknown = 0,

    /// <summary>询价（RFQ / Rate request）。</summary>
    Inquiry = 1,

    /// <summary>报价（Quotation / Offer）。</summary>
    Quotation = 2,

    /// <summary>订舱委托（客户给的 Booking Request）。</summary>
    BookingRequest = 3,

    /// <summary>订舱确认 / 放舱 / SO Release。</summary>
    BookingConfirmation = 4,

    /// <summary>补料（Shipping Instruction / SI）。</summary>
    ShippingInstruction = 5,

    /// <summary>VGM 提交 / 截止。</summary>
    Vgm = 6,

    /// <summary>提单草稿与确认（Draft B/L）。</summary>
    DraftBillOfLading = 7,

    /// <summary>到港通知（Arrival Notice / 提货通知）。</summary>
    ArrivalNotice = 8,

    /// <summary>账单 / 对账（Debit Note / Invoice）。</summary>
    DebitNote = 9,

    /// <summary>查验通知（异常）。</summary>
    CustomsInspection = 10,

    /// <summary>甩柜 / 改配（异常）。</summary>
    Rollover = 11,

    /// <summary>延误 / 跳港（异常）。</summary>
    Delay = 12
}

/// <summary>邮件类型的展示与判定属性。</summary>
public static class MailTypes
{
    /// <summary>异常类邮件（查验 / 甩柜 / 延误）：必须高优先级、不参与静音。</summary>
    public static bool IsException(MailType type)
        => type is MailType.CustomsInspection or MailType.Rollover or MailType.Delay;

    public static string LabelOf(MailType type) => type switch
    {
        MailType.Inquiry => "询价",
        MailType.Quotation => "报价",
        MailType.BookingRequest => "订舱委托",
        MailType.BookingConfirmation => "订舱确认",
        MailType.ShippingInstruction => "补料",
        MailType.Vgm => "VGM",
        MailType.DraftBillOfLading => "提单草稿",
        MailType.ArrivalNotice => "到港通知",
        MailType.DebitNote => "账单",
        MailType.CustomsInspection => "查验",
        MailType.Rollover => "甩柜",
        MailType.Delay => "延误",
        _ => ""
    };

    /// <summary>列表 / 时间轴上的短标签。</summary>
    public static string ShortOf(MailType type) => type switch
    {
        MailType.Inquiry => "询价",
        MailType.Quotation => "报价",
        MailType.BookingRequest => "委托",
        MailType.BookingConfirmation => "订舱",
        MailType.ShippingInstruction => "SI",
        MailType.Vgm => "VGM",
        MailType.DraftBillOfLading => "B/L",
        MailType.ArrivalNotice => "AN",
        MailType.DebitNote => "账单",
        MailType.CustomsInspection => "查验",
        MailType.Rollover => "甩柜",
        MailType.Delay => "延误",
        _ => ""
    };

    public static string ColorOf(MailType type) => type switch
    {
        MailType.Inquiry => "#0891b2",
        MailType.Quotation => "#7c3aed",
        MailType.BookingRequest => "#2563eb",
        MailType.BookingConfirmation => "#0ea5e9",
        MailType.ShippingInstruction => "#0d9488",
        MailType.Vgm => "#a855f7",
        MailType.DraftBillOfLading => "#8b5cf6",
        MailType.ArrivalNotice => "#22c55e",
        MailType.DebitNote => "#d97706",
        MailType.CustomsInspection => "#dc2626",
        MailType.Rollover => "#b91c1c",
        MailType.Delay => "#ea580c",
        _ => "#64748b"
    };

    /// <summary><c>type:</c> 搜索运算符的关键字映射。</summary>
    public static bool TryParseKeyword(string? value, out MailType type)
    {
        type = MailType.Unknown;
        if (string.IsNullOrWhiteSpace(value)) return false;

        type = value.Trim().ToLowerInvariant() switch
        {
            "inquiry" or "rfq" or "询价" => MailType.Inquiry,
            "quotation" or "quote" or "报价" => MailType.Quotation,
            "bookingrequest" or "委托" or "订舱委托" => MailType.BookingRequest,
            "booking" or "so" or "sorelease" or "订舱" or "订舱确认" or "放舱" => MailType.BookingConfirmation,
            "si" or "shippinginstruction" or "补料" => MailType.ShippingInstruction,
            "vgm" => MailType.Vgm,
            "bl" or "draftbl" or "提单" or "提单草稿" => MailType.DraftBillOfLading,
            "an" or "arrival" or "到港" or "到港通知" => MailType.ArrivalNotice,
            "invoice" or "debit" or "账单" or "对账" => MailType.DebitNote,
            "inspection" or "customs" or "查验" => MailType.CustomsInspection,
            "rollover" or "甩柜" or "改配" => MailType.Rollover,
            "delay" or "延误" or "跳港" => MailType.Delay,
            _ => MailType.Unknown
        };

        return type != MailType.Unknown;
    }
}

/// <summary>
/// 一次类型识别的结果：类型 + 命中强度（用于界面上弱化低置信度结果）+ 判定依据
/// （<see cref="Services.MailTypeClassifier"/> 产出的 <c>key:词@位置</c> / <c>thread:inherit</c>）。
/// </summary>
public sealed record MailTypeResult(MailType Type, int Score, string Basis = "")
{
    public static MailTypeResult Unknown { get; } = new(MailType.Unknown, 0);

    /// <summary>依据的界面文案；空串表示没有依据可展示。</summary>
    public string BasisText => Services.MailTypeClassifier.DescribeBasis(Basis);

    public bool IsKnown => Type != MailType.Unknown;

    public string Label => MailTypes.LabelOf(Type);

    public string Short => MailTypes.ShortOf(Type);

    public string Color => MailTypes.ColorOf(Type);

    public bool IsException => MailTypes.IsException(Type);
}
