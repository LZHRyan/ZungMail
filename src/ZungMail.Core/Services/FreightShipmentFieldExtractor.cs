using System.Globalization;
using System.Text.RegularExpressions;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>
/// 运输字段抽取：箱型箱量、航线对、贸易条款与运费金额。
/// <para>
/// 设计原则（与单号/时效抽取一致）：<b>宁可漏，不可错</b>。箱型必须有尺寸+类型；
/// 航线两端都必须命中港口词表（含中文名与俄语别名），且中间只允许出现分隔符；
/// 金额必须带币种、且附近出现运费类词（避免把银行电子账单的金额当运费）。
/// </para>
/// </summary>
public static class FreightShipmentFieldExtractor
{
    /// <summary>航线两端之间的最大间隔（字符）：超过就当不是一条航线，避免跨句误配。</summary>
    private const int MaxLaneGap = 24;

    /// <summary>金额与运费类词的最大距离（字符）。</summary>
    private const int AmountContextWindow = 40;

    private const RegexOptions IgnoreCase = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>箱型箱量：数量 * 尺寸 + 类型；类型缺省不算（`1*40` 这种在真实数据里多半是笔误）。</summary>
    private static readonly Regex BoxRegex = new(
        @"(?<n>\d{1,3})\s*[*xX×]\s*(?<size>20|40|45)\s*['’]?\s*(?<type>GP|HC|HQ|DV|DC|RF|OT|FR|NOR|TK|PF)\b",
        IgnoreCase);

    /// <summary>两端之间必须出现的分隔符：`- – — / → -> &gt; to`（中间允许夹国名，如「Китай, Гуанчжоу - Россия, СПб」）。</summary>
    private static readonly Regex LaneSeparatorRegex = new(
        @"(?:-|–|—|/|→|->|>|\bto\b)",
        IgnoreCase);

    /// <summary>两端之间出现数字就说明是单号里的连字符，不是航线。</summary>
    private static readonly Regex LaneGapDigitsRegex = new(@"\d", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>贸易条款（封闭集合，只认大写，避免命中普通单词）。</summary>
    private static readonly Regex IncotermRegex = new(
        @"\b(FOB|CFR|CIF|EXW|FCA|DAP|DDP|CPT|CIP)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>金额：币种在左或右都认。</summary>
    private static readonly Regex CurrencyFirstRegex = new(
        @"(?<cur>USD|US\$|CNY|RMB|EUR|₽|RUB|руб)\s*(?<amt>\d[\d ,.]{0,12})",
        IgnoreCase);

    private static readonly Regex AmountFirstRegex = new(
        @"(?<amt>\d[\d ,.]{0,12})\s*(?<cur>USD|CNY|RMB|EUR|₽|RUB|руб)\b",
        IgnoreCase);

    /// <summary>运费语境词：金额附近必须出现一个，否则不抽。</summary>
    private static readonly string[] FreightWords =
    {
        "运费", "海运费", "空运费", "费用", "本地费用", "港杂费", "报价", "单价", "价格", "费率",
        "freight", "rate", "local charge", "price", "quote", "ocean freight", "all in"
    };

    /// <summary>系统通知 / 银行账单：这类邮件的金额不是运费，直接跳过金额抽取。</summary>
    private static readonly string[] AmountExcludedMarkers =
    {
        "信用卡", "电子账单", "验证码", "一次性代码", "余额", "还款", "statement", "verification code", "one-time code"
    };

    /// <summary>
    /// 港口别名：英文 / 俄语 / 中文 → 统一展示名。UN/LOCODE 由 <see cref="FreightReferenceExtractor.KnownPorts"/> 补齐。
    /// 只收真实航线上见过的写法，宁可少收也不要让普通单词变成港口。
    /// </summary>
    private static readonly Dictionary<string, string> PortAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NINGBO"] = "宁波", ["SHANGHAI"] = "上海", ["SHENZHEN"] = "深圳", ["NANSHA"] = "南沙",
        ["YANTIAN"] = "盐田", ["XINGANG"] = "新港", ["QINGDAO"] = "青岛", ["DALIAN"] = "大连",
        ["ZHANJIANG"] = "湛江", ["GUANGZHOU"] = "广州", ["GUANGDONG"] = "广东", ["NANJING"] = "南京",
        ["LIANYUNGANG"] = "连云港", ["XIAMEN"] = "厦门", ["TIANJIN"] = "天津", ["HONG KONG"] = "香港",
        ["VRANGEL"] = "符拉迪沃斯托克", ["VRANGEL BAY"] = "符拉迪沃斯托克", ["VLADIVOSTOK"] = "符拉迪沃斯托克",
        ["VVO"] = "符拉迪沃斯托克", ["NAKHODKA"] = "纳霍德卡", ["VOSTOCHNY"] = "东方港",
        ["ST PETERSBURG"] = "圣彼得堡", ["SAINT PETERSBURG"] = "圣彼得堡", ["ST.PETERSBURG"] = "圣彼得堡",
        ["ST-PETERSBURG"] = "圣彼得堡", ["SPB"] = "圣彼得堡", ["СПБ"] = "圣彼得堡", ["САНКТ-ПЕТЕРБУРГ"] = "圣彼得堡",
        ["MOSCOW"] = "莫斯科", ["МОСКВА"] = "莫斯科",
        ["ГУАНЧЖОУ"] = "广州", ["ШАНХАЙ"] = "上海", ["НИНБО"] = "宁波", ["ШЭНЬЧЖЭНЬ"] = "深圳",
        ["ЦИНДАО"] = "青岛", ["ТЯНЬЦЗИНЬ"] = "天津", ["ДАЛЯНЬ"] = "大连", ["ЯНЬТЯНЬ"] = "盐田",
        ["BANGKOK"] = "曼谷", ["ROTTERDAM"] = "鹿特丹", ["ISTANBUL"] = "伊斯坦布尔", ["HAMBURG"] = "汉堡",
        ["SINGAPORE"] = "新加坡", ["BUSAN"] = "釜山", ["TOKYO"] = "东京", ["OSAKA"] = "大阪",
        ["HO CHI MINH"] = "胡志明", ["HAIPHONG"] = "海防", ["LAEM CHABANG"] = "林查班",
        ["JEBEL ALI"] = "杰贝阿里", ["PORT KLANG"] = "巴生港", ["COLOMBO"] = "科伦坡",
        ["宁波"] = "宁波", ["上海"] = "上海", ["深圳"] = "深圳", ["南沙"] = "南沙", ["盐田"] = "盐田",
        ["青岛"] = "青岛", ["厦门"] = "厦门", ["天津"] = "天津", ["大连"] = "大连", ["连云港"] = "连云港",
        ["湛江"] = "湛江", ["广州"] = "广州", ["新港"] = "新港", ["香港"] = "香港",
        ["符拉迪沃斯托克"] = "符拉迪沃斯托克", ["海参崴"] = "符拉迪沃斯托克", ["纳霍德卡"] = "纳霍德卡",
        ["圣彼得堡"] = "圣彼得堡", ["莫斯科"] = "莫斯科", ["曼谷"] = "曼谷", ["鹿特丹"] = "鹿特丹",
        ["伊斯坦布尔"] = "伊斯坦布尔", ["汉堡"] = "汉堡", ["新加坡"] = "新加坡", ["釜山"] = "釜山"
    };

    /// <summary>从主题（优先）与正文开头抽取运输字段。</summary>
    public static ShipmentFields Extract(string? subject, string? body)
    {
        var subjectText = Collapse(subject);
        var bodyText = Collapse(body);

        var boxes = ExtractBoxes(subjectText);
        if (boxes.Count == 0) boxes = ExtractBoxes(bodyText);

        var lane = FindLane(subjectText) ?? FindLane(bodyText);

        var incoterm = FindIncoterm(subjectText);
        var incotermSource = "subject";
        if (incoterm.Length == 0)
        {
            incoterm = FindIncoterm(bodyText);
            incotermSource = incoterm.Length > 0 ? "body" : "";
        }

        var (amount, currency) = FindAmount(subjectText, bodyText);

        var hasAny = boxes.Count > 0 || lane is not null || incoterm.Length > 0 || amount is not null;
        return hasAny
            ? new ShipmentFields(boxes, lane, incoterm, amount, currency, BuildBasis(boxes.Count > 0 ? "subject" : "", lane, incotermSource, amount))
            : ShipmentFields.Empty;
    }

    /// <summary>箱型箱量：去重 + 归一（40HQ→40HC、20DV→20DC、40PF→40FR），最多 4 条。</summary>
    public static IReadOnlyList<ShipmentBox> ExtractBoxes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<ShipmentBox>();

        var seen = new HashSet<(int, string, string)>();
        var result = new List<ShipmentBox>();

        foreach (Match match in BoxRegex.Matches(text))
        {
            if (!int.TryParse(match.Groups["n"].Value, out var count) || count <= 0 || count > 999) continue;

            var size = match.Groups["size"].Value;
            var type = NormalizeType(match.Groups["type"].Value);
            if (!seen.Add((count, size, type))) continue;

            result.Add(new ShipmentBox(count, size, type));
            if (result.Count >= 4) break;
        }

        return result;
    }

    /// <summary>在文本里找「港口 A 分隔符 港口 B」，两端都必须命中港口词表。</summary>
    public static ShipmentLane? FindLane(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var hits = FindPortHits(text);
        for (var i = 0; i + 1 < hits.Count; i++)
        {
            var left = hits[i];
            var right = hits[i + 1];
            if (right.Start < left.End) continue;

            var gap = text[left.End..right.Start];
            if (gap.Length > MaxLaneGap) continue;
            if (LaneGapDigitsRegex.IsMatch(gap)) continue;
            if (!LaneSeparatorRegex.IsMatch(gap)) continue;
            if (left.Name == right.Name) continue;

            var raw = text[left.Start..right.End].Trim();
            return new ShipmentLane(left.Name, right.Name, raw);
        }

        return null;
    }

    /// <summary>贸易条款（只认大写，主题优先由调用方决定）。</summary>
    public static string FindIncoterm(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var match = IncotermRegex.Match(text);
        return match.Success ? match.Value : "";
    }

    /// <summary>
    /// 金额：必须带币种，且附近出现运费类词；银行/系统通知类邮件直接跳过。
    /// 返回 (金额, 币种)，没有则 (null, "")。
    /// </summary>
    public static (decimal? Amount, string Currency) FindAmount(string? subject, string? body)
    {
        if (IsAmountExcluded(subject) || IsAmountExcluded(body)) return (null, "");
        if (string.IsNullOrEmpty(body)) return (null, "");

        foreach (Match match in CurrencyFirstRegex.Matches(body))
        {
            var parsed = TryDecode(match.Groups["amt"].Value, match.Groups["cur"].Value, body, match.Index, match.Length);
            if (parsed is not null) return parsed.Value;
        }

        foreach (Match match in AmountFirstRegex.Matches(body))
        {
            var parsed = TryDecode(match.Groups["amt"].Value, match.Groups["cur"].Value, body, match.Index, match.Length);
            if (parsed is not null) return parsed.Value;
        }

        return (null, "");
    }

    private static (decimal, string)? TryDecode(string amountText, string currencyText, string text, int index, int length)
    {
        // 金额附近必须有运费类词，否则多半是货物数量 / 关税 / 银行金额
        var from = Math.Max(0, index - AmountContextWindow);
        var to = Math.Min(text.Length, index + length + AmountContextWindow);
        var window = text[from..to];
        if (!FreightWords.Any(word => window.Contains(word, StringComparison.OrdinalIgnoreCase))) return null;

        var normalized = amountText.Replace(" ", "").Replace(",", "");
        if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) return null;
        if (amount <= 0) return null;

        return (amount, NormalizeCurrency(currencyText));
    }

    private static string NormalizeCurrency(string currency) => currency.ToUpperInvariant() switch
    {
        "US$" => "USD",
        "RUB" or "РУБ" => "RUB",
        "₽" => "RUB",
        _ => currency.ToUpperInvariant()
    };

    private static bool IsAmountExcluded(string? text)
        => !string.IsNullOrEmpty(text)
           && AmountExcludedMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeType(string type) => type.ToUpperInvariant() switch
    {
        "HQ" => "HC",
        "DV" => "DC",
        "PF" => "FR",
        _ => type.ToUpperInvariant()
    };

    private static string BuildBasis(string boxSource, ShipmentLane? lane, string incotermSource, decimal? amount)
    {
        var parts = new List<string>();
        if (boxSource.Length > 0) parts.Add("box:" + boxSource);
        if (lane is not null) parts.Add("lane");
        if (incotermSource.Length > 0) parts.Add("term:" + incotermSource);
        if (amount is not null) parts.Add("amount");
        return string.Join(";", parts);
    }

    /// <summary>压缩空白：金额常被换行拆开（`28\Eur`），不折叠就永远匹配不上。</summary>
    private static string Collapse(string? text)
        => string.IsNullOrWhiteSpace(text) ? "" : Regex.Replace(text, @"\s+", " ").Trim();

    private sealed record PortHit(int Start, int End, string Name);

    /// <summary>找出文本里所有港口出现位置（LOCODE 与别名，长词优先、重叠丢弃）。</summary>
    private static List<PortHit> FindPortHits(string text)
    {
        var hits = new List<PortHit>();

        foreach (var keyword in PortAliases.Keys)
        {
            foreach (Match match in Regex.Matches(text, $@"(?<![A-Za-z0-9]){Regex.Escape(keyword)}(?![A-Za-z0-9])", IgnoreCase))
            {
                hits.Add(new PortHit(match.Index, match.Index + match.Length, PortAliases[keyword]));
            }
        }

        foreach (var (code, name) in FreightReferenceExtractor.KnownPorts)
        {
            foreach (Match match in Regex.Matches(text, $@"(?<![A-Za-z0-9]){code}(?![A-Za-z0-9])"))
            {
                hits.Add(new PortHit(match.Index, match.Index + match.Length, name));
            }
        }

        // 长的优先（`VRANGEL BAY` 盖住 `VRANGEL`），重叠的丢掉
        var ordered = hits.OrderBy(hit => hit.Start).ThenByDescending(hit => hit.End - hit.Start).ToList();
        var result = new List<PortHit>();
        foreach (var hit in ordered)
        {
            if (result.Count > 0 && hit.Start < result[^1].End) continue;
            result.Add(hit);
        }

        return result;
    }
}
