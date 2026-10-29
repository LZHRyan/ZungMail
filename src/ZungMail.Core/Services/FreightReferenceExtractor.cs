using System.Text;
using System.Text.RegularExpressions;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>
/// 货代业务标识抽取器：从邮件主题与正文中识别业务编号、订舱号、提单号、箱号、
/// 封号、船名航次、港口、发票号、PO 号与空运单号。
/// <para>
/// 设计原则是「宁缺毋滥」：可结构化校验的（箱号 ISO 6346、空运单 mod 7）必须通过校验，
/// 其余标识要求带业务标签（SO No. / B/L / 柜号 …）且包含数字，避免把普通英文词当单号。
/// </para>
/// </summary>
public static class FreightReferenceExtractor
{
    /// <summary>同类标识最多保留条数，防止长正文噪声淹没列表。</summary>
    public const int MaxPerKind = 3;

    /// <summary>单封邮件最多保留条数。</summary>
    public const int MaxTotal = 24;

    private const RegexOptions CaseSensitive = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private const RegexOptions IgnoreCase = RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase;

    // 业务编号 / Job No.：英文短标签全大写，避免把普通英文 "case" 误判成单号。
    private static readonly Regex JobRegex = new(
        @"(?<![\w-])(?:JOB|SHIPMENT|FILE|CASE)\s*(?:NO\.?|NUMBER|REF(?:ERENCE)?)?\s*[.:#：]?\s*(?<v>(?=[A-Z0-9\-_/]{4,24})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{3,23})",
        CaseSensitive);

    private static readonly Regex JobCnRegex = new(
        @"(?:业务编号|业务单号|工作号|委托号|任务编号|作业号)\s*[.:#：]?\s*(?<v>(?=[A-Z0-9\-_/]{4,24})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{3,23})",
        IgnoreCase);

    // 订舱号：SO / S/O 为高频缩写，要求全大写且带数字。
    private static readonly Regex BookingShortRegex = new(
        @"(?<![\w-])S/?O\s*(?i:NO\.?|NUMBER|#)?\s*[:#：]?\s*(?<v>(?=[A-Z0-9\-_/]{4,24})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{3,23})",
        CaseSensitive);

    private static readonly Regex BookingWordRegex = new(
        @"(?<![\w-])(?:BOOKING|BKG|SHIPPING\s+ORDER)\s*(?:NO\.?|NUMBER|REF)?\s*[.:#：\-]?\s*(?<v>(?=[A-Z0-9\-_/]{4,24})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{3,23})",
        IgnoreCase);

    private static readonly Regex BookingCnRegex = new(
        @"(?:订舱号|订舱编号|订舱单号|订舱确认号)\s*[.:#：]?\s*(?<v>(?=[A-Z0-9\-_/]{4,24})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{3,23})",
        IgnoreCase);

    // 提单号：HBL / MBL / B/L；HBL: SHAXXX260917 这类含承运人前缀的形态也能命中。
    private static readonly Regex BillOfLadingRegex = new(
        @"(?<![\w-])(?:HBL|MBL|B/L|BL)\s*(?i:NO\.?|NUMBER|#)?\s*[:#：]?\s*(?<v>(?=[A-Z0-9\-_/]{5,26})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{4,25})",
        CaseSensitive);

    private static readonly Regex BillOfLadingWordRegex = new(
        @"(?:BILL\s+OF\s+LADING|SEA\s+WAYBILL|SEAWAY\s+BILL)\s*(?:NO\.?|NUMBER|#)?\s*[:#：\-]?\s*(?<v>(?=[A-Z0-9\-_/]{5,26})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{4,25})",
        IgnoreCase);

    private static readonly Regex BillOfLadingCnRegex = new(
        @"(?:提单号|提单编号|主单号|分单号|海运单号)\s*[.:#：]?\s*(?<v>(?=[A-Z0-9\-_/]{5,26})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{4,25})",
        IgnoreCase);

    // 箱号：标签 + ISO 6346；无标签时也识别但必须通过校验位。
    private static readonly Regex ContainerLabeledRegex = new(
        @"(?<![A-Za-z])(?:CONTAINER|CNTR|CTR|柜号|箱号|集装箱号|货柜号)(?![A-Za-z])\s*(?:NO\.?|NUMBER|#)?\s*[:#：]?\s*(?<v>[A-Z]{4}[ \-]?\d{7})",
        IgnoreCase);

    private static readonly Regex ContainerBareRegex = new(
        @"(?<![A-Z0-9])(?<v>[A-Z]{4}\d{7})(?![A-Z0-9])",
        CaseSensitive);

    private static readonly Regex SealRegex = new(
        @"(?<![A-Za-z])(?:SEAL|封号|铅封|封条|关封)(?![A-Za-z])\s*(?:NO\.?|NUMBER|#)?\s*[:#：]?\s*(?<v>[A-Z0-9]{4,15})",
        IgnoreCase);

    private static readonly Regex VesselRegex = new(
        @"(?<![A-Za-z])(?:VESSEL|VSL|船名航次|船名|航次)(?![A-Za-z])\s*(?:NAME)?\s*[:#：]?\s*(?<v>[^\r\n]{3,40})",
        IgnoreCase);

    private static readonly Regex PortLabeledRegex = new(
        @"(?<![A-Za-z])(?:POL|POD|PORT\s+OF\s+LOADING|PORT\s+OF\s+DISCHARGE|起运港|目的港|装货港|卸货港|起运地|目的地)(?![A-Za-z])\s*[:：]?\s*(?<v>[A-Za-z\u4e00-\u9fff][A-Za-z\u4e00-\u9fff .\-]{1,28}?)(?=[,;。\r\n]|\s+(?:POL|POD|ETD|ETA|VESSEL|起运港|目的港)\b|\z)",
        IgnoreCase);

    private static readonly Regex PortCodeRegex = new(
        @"(?<![A-Z0-9])(?<v>[A-Z]{2}[A-Z]{3})(?![A-Z0-9])",
        CaseSensitive);

    private static readonly Regex InvoiceRegex = new(
        @"(?:INVOICE|发票)\s*(?:NO\.?|NUMBER|#)?\s*[:#：\-]?\s*(?<v>(?=[A-Z0-9\-_/]{3,24})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{2,23})",
        IgnoreCase);

    private static readonly Regex InvoiceShortRegex = new(
        @"(?<![\w-])INV\s*(?i:NO\.?|NUMBER|#)?\s*[:#：]?\s*(?<v>(?=[A-Z0-9\-_/]{3,24})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{2,23})",
        CaseSensitive);

    private static readonly Regex PurchaseOrderRegex = new(
        @"(?<![\w-])(?:P\.?O\.?|PURCHASE\s+ORDER)\s*(?i:NO\.?|NUMBER|#)?\s*[:#：]?\s*(?<v>(?=[A-Z0-9\-_/]{3,24})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{2,23})",
        CaseSensitive);

    private static readonly Regex PurchaseOrderCnRegex = new(
        @"(?:采购订单|采购单号)\s*[.:#：]?\s*(?<v>(?=[A-Z0-9\-_/]{3,24})(?=[A-Z0-9\-_/]*\d)[A-Z0-9][A-Z0-9\-_/]{2,23})",
        IgnoreCase);

    private static readonly Regex AirWaybillRegex = new(
        @"(?<![A-Za-z0-9])(?<v>\d{3}(?:[ -]?\d{4}){2})(?![A-Za-z0-9])",
        CaseSensitive);

    /// <summary>常见港口 UN/LOCODE，命中即视为港口（避免任意 5 位大写串误报）。</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownPortsInternal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CNSHA"] = "上海", ["CNNGB"] = "宁波", ["CNSZX"] = "深圳", ["CNNSA"] = "南沙",
        ["CNTAO"] = "青岛", ["CNXMN"] = "厦门", ["CNTSN"] = "天津", ["CNDLC"] = "大连",
        ["CNLYG"] = "连云港", ["CNFOS"] = "佛山", ["CNHKG"] = "香港",
        ["SGSIN"] = "新加坡", ["MYPKG"] = "巴生港", ["MYTPP"] = "丹戎帕拉帕斯",
        ["THLCH"] = "林查班", ["VNSGN"] = "胡志明", ["VNHPH"] = "海防",
        ["IDJKT"] = "雅加达", ["IDSUB"] = "泗水", ["KRPUS"] = "釜山", ["KRINC"] = "仁川",
        ["JPTYO"] = "东京", ["JPYOK"] = "横滨", ["JPOSA"] = "大阪", ["JPNGO"] = "名古屋",
        ["TWKHH"] = "高雄", ["TWKEL"] = "基隆", ["TWTXG"] = "台中",
        ["INNSA"] = "那瓦西瓦", ["INMAA"] = "金奈", ["INMUN"] = "蒙德拉", ["INCOK"] = "科钦",
        ["LKCMB"] = "科伦坡", ["BDCGP"] = "吉大港", ["PKKHI"] = "卡拉奇",
        ["AEJEA"] = "杰贝阿里", ["AEAUH"] = "阿布扎比", ["SADMM"] = "达曼", ["SAJED"] = "吉达",
        ["EGPSD"] = "塞得港", ["EGALY"] = "亚历山大", ["ZADUR"] = "德班", ["NGLOS"] = "拉各斯",
        ["KEMBA"] = "蒙巴萨", ["TRIST"] = "伊斯坦布尔", ["RUNVS"] = "新罗西斯克",
        ["NLRTM"] = "鹿特丹", ["BEANR"] = "安特卫普", ["BEZEE"] = "泽布吕赫",
        ["DEHAM"] = "汉堡", ["DEBRV"] = "不来梅哈芬", ["DELEJ"] = "莱比锡",
        ["FRLEH"] = "勒阿弗尔", ["FRMRS"] = "马赛", ["GBFXT"] = "费利克斯托",
        ["GBSOU"] = "南安普顿", ["GBLGP"] = "伦敦门户", ["GBLIV"] = "利物浦",
        ["ESVLC"] = "瓦伦西亚", ["ESBCN"] = "巴塞罗那", ["ESALG"] = "阿尔赫西拉斯",
        ["ITGOA"] = "热那亚", ["ITSPE"] = "拉斯佩齐亚", ["ITTRS"] = "的里雅斯特",
        ["GRPIR"] = "比雷埃夫斯", ["PLGDN"] = "格但斯克", ["MAPTM"] = "丹吉尔",
        ["USLAX"] = "洛杉矶", ["USLGB"] = "长滩", ["USNYC"] = "纽约", ["USSAV"] = "萨凡纳",
        ["USHOU"] = "休斯敦", ["USCHI"] = "芝加哥", ["USOAK"] = "奥克兰", ["USSEA"] = "西雅图",
        ["USMIA"] = "迈阿密", ["USORF"] = "诺福克", ["USCHS"] = "查尔斯顿", ["USBAL"] = "巴尔的摩",
        ["CAVAN"] = "温哥华", ["CAMTR"] = "蒙特利尔", ["CATOR"] = "多伦多",
        ["MXZLO"] = "曼萨尼约", ["MXVER"] = "韦拉克鲁斯", ["BRSSZ"] = "桑托斯",
        ["ARBUE"] = "布宜诺斯艾利斯", ["CLVAP"] = "瓦尔帕莱索", ["PECLL"] = "卡亚俄",
        ["AUSYD"] = "悉尼", ["AUMEL"] = "墨尔本", ["AUBNE"] = "布里斯班", ["NZAKL"] = "奥克兰"
    };

    /// <summary>
    /// 已知港口（UN/LOCODE → 中文名）。航线对抽取（<see cref="FreightShipmentFieldExtractor"/>）共用这一份词表，
    /// 避免两处各维护一份导致「同一个港口一处认得、一处不认得」。
    /// </summary>
    public static IReadOnlyDictionary<string, string> KnownPorts => KnownPortsInternal;

    /// <summary>按「主题优先、正文补充」的顺序抽取，返回按业务优先级排序的标识。</summary>
    public static IReadOnlyList<FreightReference> Extract(string? subject, string? body)
    {
        var found = new List<FreightReference>();
        var seen = new HashSet<(FreightReferenceKind Kind, string Normalized)>();

        if (!string.IsNullOrWhiteSpace(subject)) Scan(subject!, ReferenceSource.Subject, found, seen);
        if (!string.IsNullOrWhiteSpace(body)) Scan(body!, ReferenceSource.Body, found, seen);

        return PrunePrefixedDuplicates(found)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Value, StringComparer.OrdinalIgnoreCase)
            .Take(MaxTotal)
            .ToList();
    }

    /// <summary>
    /// 同一封邮件里「BKG12345」与「12345」是同一个单号：保留信息更完整的带前缀形式，
    /// 避免侧栏按单号聚合时出现两条看起来不同的记录。
    /// </summary>
    private static List<FreightReference> PrunePrefixedDuplicates(List<FreightReference> found)
    {
        if (found.Count < 2) return found;

        var keep = new List<FreightReference>(found.Count);
        foreach (var reference in found.OrderByDescending(r => r.Normalized.Length))
        {
            var duplicate = keep.Any(other =>
                other.Kind == reference.Kind
                && other.Normalized.Length > reference.Normalized.Length
                && other.Normalized.EndsWith(reference.Normalized, StringComparison.Ordinal));
            if (!duplicate) keep.Add(reference);
        }

        // 保持原有顺序，便于调用方按优先级排序
        return found.Where(keep.Contains).ToList();
    }

    private static void Scan(string text, ReferenceSource source, List<FreightReference> found, HashSet<(FreightReferenceKind, string)> seen)
    {
        Collect(JobRegex, FreightReferenceKind.Job, text, source, found, seen);
        Collect(JobCnRegex, FreightReferenceKind.Job, text, source, found, seen);

        Collect(BookingShortRegex, FreightReferenceKind.Booking, text, source, found, seen);
        Collect(BookingWordRegex, FreightReferenceKind.Booking, text, source, found, seen);
        Collect(BookingCnRegex, FreightReferenceKind.Booking, text, source, found, seen);

        Collect(BillOfLadingRegex, FreightReferenceKind.BillOfLading, text, source, found, seen);
        Collect(BillOfLadingWordRegex, FreightReferenceKind.BillOfLading, text, source, found, seen);
        Collect(BillOfLadingCnRegex, FreightReferenceKind.BillOfLading, text, source, found, seen);

        Collect(ContainerLabeledRegex, FreightReferenceKind.Container, text, source, found, seen,
            value => IsValidContainer(value) ? value.Replace(" ", "").ToUpperInvariant() : null);
        Collect(ContainerBareRegex, FreightReferenceKind.Container, text, source, found, seen,
            value => IsValidContainer(value) ? value : null);

        Collect(SealRegex, FreightReferenceKind.Seal, text, source, found, seen,
            value => HasDigit(value, 1) && value.Length >= 4 ? value : null);

        Collect(VesselRegex, FreightReferenceKind.VesselVoyage, text, source, found, seen, CleanVessel);

        Collect(PortCodeRegex, FreightReferenceKind.Port, text, source, found, seen,
            value => KnownPortsInternal.ContainsKey(value) ? FormatPort(value) : null);
        Collect(PortLabeledRegex, FreightReferenceKind.Port, text, source, found, seen, CleanPortLabel);

        Collect(InvoiceRegex, FreightReferenceKind.Invoice, text, source, found, seen);
        Collect(InvoiceShortRegex, FreightReferenceKind.Invoice, text, source, found, seen);
        Collect(PurchaseOrderRegex, FreightReferenceKind.PurchaseOrder, text, source, found, seen);
        Collect(PurchaseOrderCnRegex, FreightReferenceKind.PurchaseOrder, text, source, found, seen);

        Collect(AirWaybillRegex, FreightReferenceKind.AirWaybill, text, source, found, seen,
            value => IsValidAirWaybill(value) ? new string(value.Where(char.IsDigit).ToArray()) : null);
    }

    private static void Collect(
        Regex regex,
        FreightReferenceKind kind,
        string text,
        ReferenceSource source,
        List<FreightReference> found,
        HashSet<(FreightReferenceKind, string)> seen,
        Func<string, string?>? refine = null)
    {
        if (found.Count >= MaxTotal) return;
        if (found.Count(r => r.Kind == kind) >= MaxPerKind) return;

        foreach (Match match in regex.Matches(text))
        {
            if (found.Count(r => r.Kind == kind) >= MaxPerKind) return;
            if (found.Count >= MaxTotal) return;

            var raw = Clean(match.Groups["v"].Value);
            if (raw.Length < 3) continue;

            var value = refine is null ? raw : refine(raw);
            if (value is null || value.Length < 3) continue;

            var normalized = FreightReference.Normalize(value);
            if (normalized.Length < 4) continue;
            if (!seen.Add((kind, normalized))) continue;

            found.Add(new FreightReference
            {
                Kind = kind,
                Value = value,
                Normalized = normalized,
                Source = source
            });
        }
    }

    /// <summary>去掉首尾标点与多余空白，并把连续空白压成单个空格。</summary>
    private static string Clean(string value)
    {
        var text = Regex.Replace(value.Trim(), @"\s+", " ");
        return text.Trim(' ', '.', ',', ';', ':', '#', '-', '/', '(', ')', '[', ']', '<', '>', '"', '\'', '、', '。', '，');
    }

    private static bool HasDigit(string value, int min)
    {
        var count = value.Count(char.IsDigit);
        return count >= min;
    }

    /// <summary>ISO 6346 箱号校验：4 位字母 + 7 位数字，末位为校验位。</summary>
    public static bool IsValidContainer(string? containerNumber)
    {
        if (containerNumber is null) return false;
        var value = containerNumber.Replace(" ", "").ToUpperInvariant();
        if (value.Length != 11) return false;
        for (var i = 0; i < 4; i++) if (!char.IsAsciiLetterUpper(value[i])) return false;
        for (var i = 4; i < 11; i++) if (!char.IsAsciiDigit(value[i])) return false;

        // 第 4 位为设备类别：U（货柜）/ J（可拆卸设备）/ Z（拖车与底盘）
        if (value[3] is not ('U' or 'J' or 'Z')) return false;

        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var ch = value[i];
            int number;
            if (char.IsDigit(ch))
            {
                number = ch - '0';
            }
            else
            {
                // A=10 … Z=38，跳过 11 的倍数
                var code = ch - 'A' + 10;
                if (code >= 11) code++;
                if (code >= 22) code++;
                if (code >= 33) code++;
                number = code;
            }

            sum += number * (1 << i);
        }

        var check = sum % 11 % 10;
        return check == value[10] - '0';
    }

    /// <summary>空运单号校验：前 7 位序号 mod 7 等于第 8 位；同时排除全 0 / 全同数字的伪单号。</summary>
    public static bool IsValidAirWaybill(string? awb)
    {
        if (awb is null) return false;
        var digits = new string(awb.Where(char.IsDigit).ToArray());
        if (digits.Length != 11) return false;

        // 全同数字（如 00000000000）在正文 URL、流水号里很常见，不能当作运单号
        if (digits.All(d => d == digits[0])) return false;
        if (digits.AsSpan(0, 3).SequenceEqual("000")) return false;

        var serial = int.Parse(digits.AsSpan(3, 7));
        if (serial == 0) return false;
        return serial % 7 == digits[10] - '0';
    }

    private static string? CleanVessel(string raw)
    {
        var text = raw;

        // 船名航次后面常跟 ETD / ETA / POL 等字段，截断到这些关键字
        foreach (var keyword in new[] { " ETD", " ETA", " POL", " POD", " VOYAGE", " VOY", " 航次", " 截关", " 开船", " 到港" })
        {
            var index = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (index > 0) text = text[..index];
        }

        // 双空格通常是表格列分隔
        var gap = text.IndexOf("  ", StringComparison.Ordinal);
        if (gap > 0) text = text[..gap];

        text = Clean(text);
        var letters = text.Count(char.IsLetter);
        return text.Length <= 40 && letters >= 2 && text.Any(char.IsDigit) ? text : null;
    }

    private static string FormatPort(string code)
    {
        var upper = code.ToUpperInvariant();
        return KnownPortsInternal.TryGetValue(upper, out var name) ? $"{upper} {name}" : upper;
    }

    private static string? CleanPortLabel(string raw)
    {
        var text = Clean(raw);
        // 纯 UN/LOCODE 由 PortCodeRegex 处理；这里只保留含字母且长度合理的港口名
        return text.Length is >= 2 and <= 30 && text.Any(char.IsLetter) ? text : null;
    }
}
