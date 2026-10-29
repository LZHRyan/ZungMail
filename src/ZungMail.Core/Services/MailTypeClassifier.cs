using System.Text.RegularExpressions;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>
/// 行业邮件类型识别：把一封邮件归入订舱 / 补料 / 提单 / 账单 / 异常等类型。
/// <para>
/// 判定分四步，优先级从高到低：
/// ① 主题里的「强词」（订单确认、提单样本、补料、查验…）——异常类永远优先，其余按在主题里出现的位置靠前者优先；
/// ② 正文开头的强词（只看前 <see cref="BodyStrongWindow"/> 字，避免把引用里的旧单据当成这封的类型）；
/// ③ 弱词（booking / 账单 / 对账 …）——必须同时抽到业务单号或箱型箱量，防止银行电子账单被当成运费账单；
/// ④ 既有关键词打分（主题 +3 / 正文 +1，阈值 2）兜底。
/// </para>
/// 每次判定都产出「依据」（命中的词与位置 / 继承会话），供界面解释「为什么是这一类」。
/// </summary>
public static class MailTypeClassifier
{
    private const int SubjectWeight = 3;
    private const int BodyWeight = 1;
    private const int MinimumScore = 2;
    private const int MaxScore = 9;

    /// <summary>正文强词只在这个字符窗口内生效：新内容通常在开头，引用在底部。</summary>
    private const int BodyStrongWindow = 300;

    private sealed record Rule(MailType Type, string[] Keywords);

    /// <summary>
    /// 强词：货代语境下几乎不会误伤的单据 / 业务名，单独命中即可判定类型。
    /// 注意「账单 / 对账」这类通用词不能进强词表——银行「电子对账单」会全部命中（见测试）。
    /// </summary>
    private static readonly Rule[] StrongRules =
    {
        new(MailType.CustomsInspection, new[]
        {
            "customs inspection", "inspection notice", "查验通知", "海关查验", "开箱查验", "таможенный досмотр", "досмотр груза"
        }),

        new(MailType.Rollover, new[]
        {
            "rollover", "roll over", "rolled to", "cut and run", "甩柜", "改配",
            "ролловер", "откат на следующее судно", "перенос на следующее судно"
        }),

        new(MailType.Delay, new[]
        {
            "vessel delay", "delay notice", "port omission", "omit port", "延误通知", "跳港", "晚开", "晚到",
            "задержка судна", "судно задерж", "пропуск порта"
        }),

        new(MailType.BookingConfirmation, new[]
        {
            "booking confirmation", "booking confirmed", "so no", "so release", "shipping order",
            "space confirmed", "订舱确认", "放舱", "舱位确认",
            "букинг подтвержд", "подтверждаем бронирован", "подтверждение бронирован", "слот подтвержд"
        }),

        new(MailType.DraftBillOfLading, new[]
        {
            "draft b/l", "draft bl", "draft bill of lading", "bl draft", "b/l draft",
            "b/l copy", "copy of b/l", "bl sample",
            "提单草稿", "提单确认", "提单核对", "提单样本", "提单样式", "开船提单", "电放保函"
        }),

        new(MailType.ShippingInstruction, new[]
        {
            "shipping instruction", "si cut-off", "si cut off", "si cut", "补料", "补料通知", "补料确认", "补料回执",
            "以此为准", "инструкция по отгрузке", "si предоставлен", "прилагаем si"
        }),

        new(MailType.Vgm, new[] { "vgm" }),

        new(MailType.ArrivalNotice, new[]
        {
            "arrival notice", "arrival notification", "到港通知", "提货通知", "到港安排"
        }),

        new(MailType.DebitNote, new[]
        {
            "debit note", "statement of account", "freight invoice", "invoice no",
            "运费结算", "港杂费", "本地费用", "应收账单", "应付账单", "费用确认",
            "счёт на оплату", "счет на оплату", "счёт-фактура", "акт сверки"
        }),

        new(MailType.BookingRequest, new[]
        {
            "booking request", "booking form", "please book", "订舱委托", "托书", "请订舱",
            "просим забронировать", "забронируйте", "оформить букинг", "прошу букинг"
        }),

        new(MailType.Quotation, new[]
        {
            "quotation", "quote for", "rate offer", "our offer", "报价单", "报价如下", "报盘",
            "котировк", "прайс-лист", "прайс лист", "наши тариф", "тарифы на", "ставки на", "предложение по ставк"
        }),

        new(MailType.Inquiry, new[]
        {
            "rfq", "rate request", "rate inquiry", "询价", "请报价", "报价请求",
            "запрос", "заявка на", "прошу сообщить стоимость", "рассчитать стоимость", "стоимость перевозки"
        })
    };

    /// <summary>
    /// 弱词：容易误伤的词，必须与业务信号（抽到单号 / 出现箱型箱量）同时成立才计分。
    /// </summary>
    private static readonly Rule[] WeakRules =
    {
        new(MailType.BookingConfirmation, new[] { "booking", "букинг" }),
        new(MailType.Quotation, new[] { "тариф", "ставк", "rate" }),
        new(MailType.DebitNote, new[] { "账单", "对账单", "费用明细", "invoice", "инвойс" }),
        new(MailType.DraftBillOfLading, new[] { "提单", "b/l", "bl" })
    };

    /// <summary>
    /// 既有关键词表：作为最后兜底（主题 +3 / 正文 +1，阈值 2）。
    /// 只收「货代语境下几乎不会误伤」的短语；通用词「账单 / 对账单」不能单独使用。
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

    /// <summary>箱型箱量（1*40HC / 3x20GP）：出现即说明是业务邮件，可放行弱词。</summary>
    private static readonly Regex BoxSpecRegex = new(
        @"\b\d{1,3}\s*[*xX×]\s*(20|40|45)\s*['’]?\s*(GP|HC|HQ|DC|DV|RF|OT|FR|NOR|TK)?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>大写 SI（Shipping Instruction 的行业缩写）：小写 si 在其它语言里太常见，只认大写。</summary>
    private static readonly Regex SiTokenRegex = new(
        @"(?<![A-Za-z])SI(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 引用分隔符：正文强词只看「这封邮件自己写的内容」，引用里的旧单据不算。
    /// 命中即在这里截断（和摘要输入构造同一套思路，但只做最小判断，不改写原文）。
    /// </summary>
    private static readonly Regex QuoteSeparatorRegex = new(
        @"(?m)^\s*(?:-{2,}\s*(?:原始邮件|Original Message|Forwarded message)|发件人\s*[:：]|From\s*:\s|在.{0,40}写道\s*[:：]|On\s.{0,80}wrote\s*:)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 转发头：正文里出现这种「From:/发件人:」段落说明后面是旧信，正文强词不再参与判定，
    /// 否则转发里提到的旧单据会把当前邮件带偏。
    /// </summary>
    private static readonly Regex ForwardHeaderRegex = new(
        @"(?m)^\s*(?:from|发件人|sent|发送时间)\s*[:：]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PrefixRegex = new(
        @"^\s*(?:(?:re|fw|fwd)\s*[:：]|回复\s*[:：]|转发\s*[:：])\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>识别一封邮件的类型；无法判断时返回 <see cref="MailTypeResult.Unknown"/>。</summary>
    /// <param name="hasBusinessSignal">是否已从这封邮件抽到业务单号（由索引层传入）；与箱型箱量一起用于放行弱词。</param>
    public static MailTypeResult Classify(string? subject, string? body, bool hasBusinessSignal = false)
    {
        var subjectText = Normalize(subject);

        // 引用与转发判断必须在压缩空白之前做：Normalize 会把换行折成空格，行的开头就没了
        var bodyText = Normalize(TrimQuoted(body ?? ""));

        if (subjectText.Length == 0 && bodyText.Length == 0) return MailTypeResult.Unknown;

        // 业务信号：抽到单号，或主题/正文出现箱型箱量——弱词只在这时参与判定
        var signal = hasBusinessSignal
                     || BoxSpecRegex.IsMatch(subjectText)
                     || BoxSpecRegex.IsMatch(bodyText);

        // ① 主题候选：强词总参与；弱词仅有业务信号时参与。异常优先，其次位置靠前
        var subjectCandidates = CollectCandidates(subjectText, includeWeak: signal);
        if (subjectCandidates.Count > 0)
        {
            var winner = PickWinner(subjectCandidates);
            return new MailTypeResult(winner.Type, SubjectWeight, Basis(winner.Keyword, $"@{winner.Position}"));
        }

        // ② 正文候选：只看「自己写的那段」的开头窗口，且只认强词（正文单个弱词只有 1 分，不够判类型）。
        // 引用/转发部分已在 TrimQuoted 里截掉，不会把旧信里的单据算到这封头上。
        var bodyWindow = bodyText.Length > BodyStrongWindow ? bodyText[..BodyStrongWindow] : bodyText;
        var bodyCandidates = CollectCandidates(bodyWindow, includeWeak: false);
        if (bodyCandidates.Count > 0)
        {
            var exceptionHit = bodyCandidates
                .Where(hit => MailTypes.IsException(hit.Type))
                .OrderBy(hit => hit.Position).FirstOrDefault();
            if (exceptionHit.Keyword is not null)
            {
                return new MailTypeResult(exceptionHit.Type, MinimumScore, Basis(exceptionHit.Keyword, "@body"));
            }

            var distinctTypes = bodyCandidates.Select(hit => hit.Type).Distinct().ToList();
            if (distinctTypes.Count == 1)
            {
                var hit = bodyCandidates[0];
                return new MailTypeResult(hit.Type, MinimumScore, Basis(hit.Keyword, "@body"));
            }

            // 正文同时出现多类单据（常见于长引用）：不猜，交给人工与后续规则
            return MailTypeResult.Unknown;
        }

        // ③ 既有打分兜底
        MailType bestType = MailType.Unknown;
        var bestScore = 0;
        var bestIsException = false;
        string? bestKeyword = null;
        var bestWhere = "";

        foreach (var rule in Rules)
        {
            var score = 0;
            string? hitKeyword = null;
            var hitWhere = "";

            foreach (var keyword in rule.Keywords)
            {
                if (subjectText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    score += SubjectWeight;
                    hitKeyword ??= keyword;
                    if (hitWhere.Length == 0) hitWhere = "@subject";
                }
                else if (bodyText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    score += BodyWeight;
                    hitKeyword ??= keyword;
                    if (hitWhere.Length == 0) hitWhere = "@body";
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
                bestKeyword = hitKeyword;
                bestWhere = hitWhere;
            }
        }

        if (bestScore < MinimumScore) return MailTypeResult.Unknown;
        return new MailTypeResult(bestType, Math.Min(bestScore, MaxScore),
            bestKeyword is null ? "" : Basis(bestKeyword, bestWhere));
    }

    /// <summary>
    /// 收集一段文本里的类型候选：强词总收，弱词按 <paramref name="includeWeak"/> 决定。
    /// 同一个关键词只记第一次出现的位置（「位次」就是它）。
    /// </summary>
    private static List<(MailType Type, string Keyword, int Position)> CollectCandidates(string text, bool includeWeak)
    {
        var candidates = new List<(MailType Type, string Keyword, int Position)>();
        if (text.Length == 0) return candidates;

        foreach (var rule in StrongRules)
        {
            foreach (var keyword in rule.Keywords)
            {
                var index = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                if (index >= 0) candidates.Add((rule.Type, keyword, index));
            }
        }

        var siIndex = SiTokenRegex.Match(text);
        if (siIndex.Success) candidates.Add((MailType.ShippingInstruction, "SI", siIndex.Index));

        if (includeWeak)
        {
            foreach (var rule in WeakRules)
            {
                foreach (var keyword in rule.Keywords)
                {
                    var index = FindBounded(text, keyword);
                    if (index >= 0) candidates.Add((rule.Type, keyword, index));
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// 弱词必须整词命中：`bl` 不能匹配 `MBL`/`HBL`，`rate` 不能匹配 `generate`。
    /// 中文没有词边界问题，同一套前后断言对 CJK 同样成立。
    /// </summary>
    private static int FindBounded(string text, string keyword)
    {
        var regex = BoundedRegexCache.GetOrAdd(keyword, static word => new Regex(
            $@"(?<![A-Za-z0-9]){Regex.Escape(word)}(?![A-Za-z0-9])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        var match = regex.Match(text);
        return match.Success ? match.Index : -1;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> BoundedRegexCache = new();

    /// <summary>异常类永远优先；同优先级取主题里位置最靠前的；再同则按关键词长度（长词更具体）。</summary>
    private static (MailType Type, string Keyword, int Position) PickWinner(
        List<(MailType Type, string Keyword, int Position)> candidates)
        => candidates
            .OrderByDescending(candidate => MailTypes.IsException(candidate.Type))
            .ThenBy(candidate => candidate.Position)
            .ThenByDescending(candidate => candidate.Keyword.Length)
            .First();

    /// <summary>依据串：<c>key:提单样本@12</c>、<c>key:booking@body</c>。</summary>
    public static string Basis(string keyword, string where) => $"key:{keyword}{where}";

    /// <summary>会话继承的依据串。</summary>
    public const string ThreadBasis = "thread:inherit";

    /// <summary>把依据串翻译成界面文案；空串返回空。</summary>
    public static string DescribeBasis(string? basis)
    {
        if (string.IsNullOrWhiteSpace(basis)) return "";
        if (basis == ThreadBasis) return "继承会话类型";

        if (!basis.StartsWith("key:", StringComparison.Ordinal)) return basis;

        var payload = basis[4..];
        var at = payload.LastIndexOf('@');
        if (at <= 0) return $"命中「{payload}」";

        var keyword = payload[..at];
        var where = payload[(at + 1)..];
        if (where == "body") return $"命中「{keyword}」（正文）";
        if (where == "subject") return $"命中「{keyword}」（主题）";
        return int.TryParse(where, out var position)
            ? $"命中「{keyword}」（主题第 {position} 字）"
            : $"命中「{keyword}」";
    }

    /// <summary>截断引用历史：只保留这封邮件自己写的内容；没有分隔符时原样返回。</summary>
    private static string TrimQuoted(string body)
    {
        if (body.Length == 0) return body;

        var match = QuoteSeparatorRegex.Match(body);
        if (match.Success && match.Index > 0) return body[..match.Index].TrimEnd();
        return body;
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
