namespace ZungMail.Core.Domain;

/// <summary>箱型箱量：`1*40HC` → <c>1×40HC</c>。数字一律来自原文，抽取过程不改写。</summary>
public sealed record ShipmentBox(int Count, string Size, string Type)
{
    /// <summary>界面展示用：`1×40HC`。</summary>
    public string Display => $"{Count}×{Size}{Type}";
}

/// <summary>航线对：起运港 → 目的港。两端都必须命中港口词表。</summary>
public sealed record ShipmentLane(string From, string To, string Raw)
{
    public string Display => $"{From} → {To}";
}

/// <summary>
/// 一封邮件里的运输字段：箱型箱量、航线、贸易条款、金额。
/// 全部由规则抽取（见 <c>FreightShipmentFieldExtractor</c>），不经过模型，数字与港口不做任何改写。
/// </summary>
public sealed record ShipmentFields(
    IReadOnlyList<ShipmentBox> Boxes,
    ShipmentLane? Lane,
    string Incoterm,
    decimal? Amount,
    string Currency,
    string Basis)
{
    public static ShipmentFields Empty { get; } = new(Array.Empty<ShipmentBox>(), null, "", null, "", "");

    /// <summary>是否有任何字段（决定详情卡要不要显示这一行）。</summary>
    public bool HasAny => Boxes.Count > 0 || Lane is not null || Incoterm.Length > 0 || Amount is not null;

    /// <summary>落库与搜索用的规范化箱型串：`1×40HC;3×20GP`。</summary>
    public string BoxesText => string.Join(";", Boxes.Select(box => box.Display));

    /// <summary>金额展示：`300 USD`；没有金额时为空。</summary>
    public string AmountDisplay => Amount is null ? "" : $"{Amount.Value:0.##} {Currency}";
}
