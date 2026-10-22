namespace ZungMail.Core.Domain;

/// <summary>
/// 货代业务标识类型。货代邮件里真正的主键不是主题而是这些单号：
/// 一票货的全部往来邮件靠业务编号 / 订舱号 / 提单号 / 箱号串起来。
/// </summary>
public enum FreightReferenceKind
{
    /// <summary>业务编号（Job No. / 委托号），货代内部一票货的主键。</summary>
    Job = 0,

    /// <summary>订舱号（SO / Booking No. / BKG No.）。</summary>
    Booking = 1,

    /// <summary>提单号（HBL / MBL / B/L No.）。</summary>
    BillOfLading = 2,

    /// <summary>集装箱号（ISO 6346）。</summary>
    Container = 3,

    /// <summary>封号（Seal No.）。</summary>
    Seal = 4,

    /// <summary>船名航次（Vessel / Voyage）。</summary>
    VesselVoyage = 5,

    /// <summary>港口（起运港 / 目的港 / UN-LOCODE）。</summary>
    Port = 6,

    /// <summary>发票号（Invoice No.）。</summary>
    Invoice = 7,

    /// <summary>采购订单号（PO No.）。</summary>
    PurchaseOrder = 8,

    /// <summary>空运单号（AWB，11 位含校验位）。</summary>
    AirWaybill = 9
}

/// <summary>标识的抽取来源：主题优先于正文（主题里的单号通常是业务主单号）。</summary>
public enum ReferenceSource
{
    Subject = 0,
    Body = 1
}

/// <summary>
/// 邮件中抽取出的一个货代业务标识。<see cref="Normalized"/> 只保留字母数字，
/// 用于跨邮件聚合（<c>SO-2026-001</c> 与 <c>SO2026001</c> 视为同一票）。
/// </summary>
public sealed class FreightReference
{
    public FreightReferenceKind Kind { get; init; }

    /// <summary>原始展示值（已去除首尾标点）。</summary>
    public string Value { get; init; } = "";

    /// <summary>聚合键：大写、仅字母数字。</summary>
    public string Normalized { get; init; } = "";

    public ReferenceSource Source { get; init; }

    /// <summary>中文类型名，例如「订舱号」。</summary>
    public string KindLabel => KindLabelOf(Kind);

    /// <summary>列表徽标用的短标签，例如「SO」。</summary>
    public string KindShort => KindShortOf(Kind);

    /// <summary>类型主色，用于徽标与详情卡片。</summary>
    public string KindColor => KindColorOf(Kind);

    /// <summary>把该标识转换成可在搜索框直接执行的过滤表达式。</summary>
    public string SearchText => $"{SearchKeywordOf(Kind)}:{Value}";

    /// <summary>列表徽标只展示优先级最高的一条，数字越小越优先。</summary>
    public int Priority => Kind switch
    {
        FreightReferenceKind.Job => 0,
        FreightReferenceKind.Booking => 1,
        FreightReferenceKind.BillOfLading => 2,
        FreightReferenceKind.Container => 3,
        FreightReferenceKind.AirWaybill => 4,
        FreightReferenceKind.Invoice => 5,
        FreightReferenceKind.PurchaseOrder => 6,
        FreightReferenceKind.VesselVoyage => 7,
        FreightReferenceKind.Port => 8,
        _ => 9
    };

    public string Display => $"{KindLabel} {Value}";

    /// <summary>搜索运算符关键字（小写，可直接用于搜索框）。</summary>
    public static string SearchKeywordOf(FreightReferenceKind kind) => kind switch
    {
        FreightReferenceKind.Job => "job",
        FreightReferenceKind.Booking => "booking",
        FreightReferenceKind.BillOfLading => "bl",
        FreightReferenceKind.Container => "ctr",
        FreightReferenceKind.Seal => "seal",
        FreightReferenceKind.VesselVoyage => "vessel",
        FreightReferenceKind.Port => "port",
        FreightReferenceKind.Invoice => "inv",
        FreightReferenceKind.PurchaseOrder => "po",
        _ => "awb"
    };

    public static string KindLabelOf(FreightReferenceKind kind) => kind switch
    {
        FreightReferenceKind.Job => "业务编号",
        FreightReferenceKind.Booking => "订舱号",
        FreightReferenceKind.BillOfLading => "提单号",
        FreightReferenceKind.Container => "箱号",
        FreightReferenceKind.Seal => "封号",
        FreightReferenceKind.VesselVoyage => "船名航次",
        FreightReferenceKind.Port => "港口",
        FreightReferenceKind.Invoice => "发票号",
        FreightReferenceKind.PurchaseOrder => "PO 号",
        _ => "空运单号"
    };

    public static string KindShortOf(FreightReferenceKind kind) => kind switch
    {
        FreightReferenceKind.Job => "JOB",
        FreightReferenceKind.Booking => "SO",
        FreightReferenceKind.BillOfLading => "B/L",
        FreightReferenceKind.Container => "CTR",
        FreightReferenceKind.Seal => "SEAL",
        FreightReferenceKind.VesselVoyage => "VSL",
        FreightReferenceKind.Port => "PORT",
        FreightReferenceKind.Invoice => "INV",
        FreightReferenceKind.PurchaseOrder => "PO",
        _ => "AWB"
    };

    public static string KindColorOf(FreightReferenceKind kind) => kind switch
    {
        FreightReferenceKind.Job => "#2563eb",
        FreightReferenceKind.Booking => "#0ea5e9",
        FreightReferenceKind.BillOfLading => "#8b5cf6",
        FreightReferenceKind.Container => "#f59e0b",
        FreightReferenceKind.Seal => "#64748b",
        FreightReferenceKind.VesselVoyage => "#14b8a6",
        FreightReferenceKind.Port => "#22c55e",
        FreightReferenceKind.Invoice => "#ef4444",
        FreightReferenceKind.PurchaseOrder => "#a855f7",
        _ => "#0891b2"
    };

    /// <summary>聚合键：仅保留字母数字并统一大写。</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var buffer = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) buffer.Append(char.ToUpperInvariant(ch));
        }
        return buffer.ToString();
    }
}
