using System.Globalization;
using System.Text.RegularExpressions;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>
/// 货代时效节点抽取器：识别截关 / 截补料 / 截 VGM / 截单证 / CY 截关，以及 ETD / ETA，
/// 并把紧邻关键词的时间解析成确定时间点。
/// <para>
/// 硬约束（见 docs/货代行业邮箱工作流调研.md 2.2）：只命中关键词不算节点——
/// 必须在关键词附近解析出合法日期时间，否则不产生提醒。只有词没有时间等于噪声。
/// </para>
/// <para>
/// 时区口径：邮件里的时间按本机本地时区解释，统一换算成 UTC 落库，展示时再转回本地。
/// 港口当地时区需要船司 / 港口主数据才能准确还原，一期不做猜测。
/// 日期缺年份时按当前年推断，若已过去 183 天以上则视为下一年（跨年场景）。
/// </para>
/// </summary>
public static class FreightDeadlineExtractor
{
    /// <summary>一封邮件最多保留的时效节点数，避免长正文把看板刷满。</summary>
    public const int MaxTotal = 8;

    /// <summary>关键词之后允许搜索时间的字符数（表格里常见「标签：换行 时间」）。</summary>
    private const int ForwardWindow = 96;

    /// <summary>时间写在关键词之前时（「2026-03-15 12:00 截关」）向前回溯的字符数。</summary>
    private const int BackwardWindow = 44;

    private static readonly RegexOptions Options =
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase;

    private static readonly Regex IsoDate = new(
        @"(?<!\d)(?<y>\d{4})\s*[-/.年]\s*(?<m>\d{1,2})\s*[-/.月]\s*(?<d>\d{1,2})\s*[日号]?",
        Options);

    private static readonly Regex ChineseMonthDay = new(
        @"(?<!\d)(?<m>\d{1,2})\s*月\s*(?<d>\d{1,2})\s*[日号]?",
        Options);

    // 15 MAR 2026 / 15-Mar-26 / 15 MARCH
    private static readonly Regex DayMonthName = new(
        @"(?<!\d)(?<d>\d{1,2})(?:st|nd|rd|th)?[\s\-.]+(?<mon>[A-Za-z]{3,9})\.?(?:[\s,\-.]+(?<y>\d{4}|\d{2})(?!\d))?",
        Options);

    // MAR 15 2026 / MAR 15
    private static readonly Regex MonthDayName = new(
        @"(?<![A-Za-z])(?<mon>[A-Za-z]{3,9})\.?[\s\-.]+(?<d>\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(?<y>\d{4})(?!\d))?",
        Options);

    // 2026/03/15、3/15、5/6/24
    private static readonly Regex SlashDate = new(
        @"(?<![\d/])(?<m>\d{1,2})/(?<d>\d{1,2})(?:/(?<y>\d{4}|\d{2})(?!\d))?",
        Options);

    // 日期后面的时间：12:00 / 17：30 / PM 3:00
    private static readonly Regex TimeOfDay = new(
        @"^[ \t]*(?:T|AT|@|,)?[ \t]*(?<ampm>上午|下午|中午|AM|PM)?[ \t]*(?<h>\d{1,2})\s*[:：]\s*(?<mi>\d{2})(?!\d)",
        Options);

    private sealed record KeywordRule(FreightDeadlineKind Kind, Regex Pattern);

    /// <summary>
    /// 关键词表：**具体类型在前**，通用 CUT OFF 兜底。
    /// 已命中的关键词与时间会标记为「已消费」，后续规则不会把同一个时间算成两个节点。
    /// </summary>
    private static readonly KeywordRule[] Rules =
    {
        new(FreightDeadlineKind.VgmCutOff, new Regex(
            @"VGM\s*CUT[\s\-]?OFF|VGM\s*截止|截\s*VGM", Options)),

        new(FreightDeadlineKind.SiCutOff, new Regex(
            @"(?<![A-Za-z])S/?I\s*CUT[\s\-]?OFF|截\s*补料|补料截止|SHIPPING\s+INSTRUCTION\s+CUT[\s\-]?OFF|SI\s*截止",
            Options)),

        new(FreightDeadlineKind.DocCutOff, new Regex(
            @"DOC(?:S)?\s+CUT[\s\-]?OFF|DOCUMENT(?:ATION)?\s*CUT[\s\-]?OFF|截\s*单证|截\s*單",
            Options)),

        new(FreightDeadlineKind.CyCutOff, new Regex(
            @"CY\s*CUT[\s\-]?OFF|CY\s*CLOSING|CY\s*CLOSE|CLOSING\s+(?:TIME|DATE|AT)|截\s*港",
            Options)),

        new(FreightDeadlineKind.CutOff, new Regex(
            @"CUT[\s\-]?OFF|CUTOFF|截\s*关|截\s*關|截\s*重柜|截\s*重櫃|STOP\s+RECEIVING|LAST\s+CARGO",
            Options)),

        new(FreightDeadlineKind.Etd, new Regex(
            @"(?<![A-Za-z])ETD(?![A-Za-z])|开船(?:日期|时间)?|预计开船|预计离港|离港日期", Options)),

        new(FreightDeadlineKind.Eta, new Regex(
            @"(?<![A-Za-z])ETA(?![A-Za-z])|到港(?:日期|时间)?|预计到港|抵港(?:日期|时间)?", Options))
    };

    /// <summary>按「主题优先、正文补充」抽取，返回按到期时间升序排列的节点。</summary>
    public static IReadOnlyList<FreightDeadline> Extract(
        string? subject,
        string? body,
        DateTimeOffset? reference = null)
    {
        var now = reference ?? DateTimeOffset.Now;
        var results = new List<FreightDeadline>();
        var seen = new HashSet<(FreightDeadlineKind Kind, long Ticks)>();

        Scan(subject, ReferenceSource.Subject, now, results, seen);
        Scan(body, ReferenceSource.Body, now, results, seen);

        return results
            .OrderBy(d => d.DueUtc)
            .ThenBy(d => d.Kind)
            .Take(MaxTotal)
            .ToList();
    }

    private static void Scan(
        string? text,
        ReferenceSource source,
        DateTimeOffset now,
        List<FreightDeadline> results,
        HashSet<(FreightDeadlineKind, long)> seen)
    {
        if (string.IsNullOrWhiteSpace(text) || results.Count >= MaxTotal) return;

        var consumed = new List<(int Start, int End)>();

        foreach (var rule in Rules)
        {
            foreach (Match match in rule.Pattern.Matches(text))
            {
                if (results.Count >= MaxTotal) return;
                if (consumed.Any(range => match.Index >= range.Start && match.Index < range.End)) continue;

                var keywordEnd = match.Index + match.Length;
                if (!TryReadDue(text, keywordEnd, now, out var due, out var end)) continue;

                consumed.Add((match.Index, end));
                if (!seen.Add((rule.Kind, due.UtcTicks))) continue;

                var rawLength = Math.Min(end - match.Index, 72);
                results.Add(new FreightDeadline
                {
                    Kind = rule.Kind,
                    DueUtc = due,
                    RawText = Clean(text.Substring(match.Index, rawLength)),
                    Source = source
                });
            }
        }
    }

    /// <summary>在关键词之后（找不到再往前）解析时间；解析失败即视为「只有词没有时间」。</summary>
    private static bool TryReadDue(
        string text,
        int keywordEnd,
        DateTimeOffset now,
        out DateTimeOffset due,
        out int end)
    {
        due = default;
        end = keywordEnd;

        var forwardLength = Math.Min(ForwardWindow, text.Length - keywordEnd);
        if (forwardLength > 0)
        {
            var window = ClipWindow(text.Substring(keywordEnd, forwardLength));
            if (TryFindDate(window, now, out due, out var index, out var length))
            {
                end = keywordEnd + index + length;
                return true;
            }
        }

        var backwardStart = Math.Max(0, keywordEnd - ForwardWindow - BackwardWindow);
        var backwardLength = keywordEnd - backwardStart;
        if (backwardLength <= 0) return false;

        var before = text.Substring(backwardStart, backwardLength);
        if (!TryFindDate(before, now, out due, out var beforeIndex, out var beforeLength)) return false;

        // 时间写在关键词之前：end 仍然落在关键词末尾，便于消费整段原文
        end = keywordEnd;
        return end > 0 && beforeIndex >= 0 && beforeLength > 0;
    }

    /// <summary>窗口截断：遇到空行或另一个时效关键词就停止，避免把下一行的时间算到本节点上。</summary>
    private static string ClipWindow(string window)
    {
        var limit = window.Length;

        var blankLine = window.IndexOf("\n\n", StringComparison.Ordinal);
        if (blankLine >= 0) limit = Math.Min(limit, blankLine + 1);

        foreach (var rule in Rules)
        {
            var match = rule.Pattern.Match(window);
            if (match.Success && match.Index > 0) limit = Math.Min(limit, match.Index);
        }

        return limit <= 0 ? window : window[..limit];
    }

    private static bool TryFindDate(
        string window,
        DateTimeOffset now,
        out DateTimeOffset due,
        out int index,
        out int length)
    {
        due = default;
        index = -1;
        length = 0;

        // 同一段里可能有多个时间（「ETD 3/15 ETA 4/20」已被 ClipWindow 切开）；
        // 取最早出现的那一个，同位置取更长的匹配（避免 3/15 被 3/15/26 截断）。
        foreach (var (pattern, parse) in Patterns)
        {
            foreach (Match match in pattern.Matches(window))
            {
                if (!parse(match, now, out var candidate)) continue;

                if (index < 0 || match.Index < index || (match.Index == index && match.Length > length))
                {
                    due = candidate;
                    index = match.Index;
                    length = match.Length;
                }
                break;
            }
        }

        if (index < 0) return false;

        // 日期后面紧跟时间时合并；没有时间按当日 23:59 处理（截关当天仍可赶）。
        var afterDate = index + length;
        var tail = window[afterDate..];
        var hour = 23;
        var minute = 59;
        var timeLength = 0;

        var timeMatch = TimeOfDay.Match(tail);
        if (timeMatch.Success
            && int.TryParse(timeMatch.Groups["h"].Value, out var parsedHour)
            && int.TryParse(timeMatch.Groups["mi"].Value, out var parsedMinute)
            && parsedHour is >= 0 and <= 23
            && parsedMinute is >= 0 and <= 59)
        {
            var ampm = timeMatch.Groups["ampm"].Value.ToUpperInvariant();
            if (ampm is "PM" or "下午" && parsedHour < 12) parsedHour += 12;
            if (ampm is "AM" or "上午" && parsedHour == 12) parsedHour = 0;
            hour = parsedHour;
            minute = parsedMinute;
            timeLength = timeMatch.Length;
        }
        else
        {
            // 没有时间信息时把到期点落在当日 23:59（原始秒已由上面设定）
            hour = 23;
            minute = 59;
        }

        length += timeLength;
        var local = new DateTime(due.Year, due.Month, due.Day, hour, minute, 0, DateTimeKind.Unspecified);
        due = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
        return true;
    }

    private static readonly (Regex Pattern, DateParser Parse)[] Patterns =
    {
        (IsoDate, ParseIso),
        (ChineseMonthDay, ParseChineseMonthDay),
        (SlashDate, ParseSlash),
        (DayMonthName, ParseDayMonthName),
        (MonthDayName, ParseMonthDayName)
    };

    private delegate bool DateParser(Match match, DateTimeOffset now, out DateTimeOffset value);

    private static bool ParseIso(Match match, DateTimeOffset now, out DateTimeOffset value)
        => Compose(
            ParseInt(match.Groups["y"].Value),
            ParseInt(match.Groups["m"].Value),
            ParseInt(match.Groups["d"].Value),
            now,
            out value);

    private static bool ParseChineseMonthDay(Match match, DateTimeOffset now, out DateTimeOffset value)
        => Compose(
            null,
            ParseInt(match.Groups["m"].Value),
            ParseInt(match.Groups["d"].Value),
            now,
            out value);

    private static bool ParseSlash(Match match, DateTimeOffset now, out DateTimeOffset value)
    {
        // 惯例按 月/日 解释（5/6/24 = 5 月 6 日）；日月序各公司口径不同，原文保留在 RawText 里可回看。
        var year = match.Groups["y"].Success ? ParseInt(match.Groups["y"].Value) : (int?)null;
        return Compose(year, ParseInt(match.Groups["m"].Value), ParseInt(match.Groups["d"].Value), now, out value);
    }

    private static bool ParseDayMonthName(Match match, DateTimeOffset now, out DateTimeOffset value)
    {
        value = default;
        if (!TryParseMonth(match.Groups["mon"].Value, out var month)) return false;

        var year = match.Groups["y"].Success ? ParseInt(match.Groups["y"].Value) : (int?)null;
        return Compose(year, month, ParseInt(match.Groups["d"].Value), now, out value);
    }

    private static bool ParseMonthDayName(Match match, DateTimeOffset now, out DateTimeOffset value)
    {
        value = default;
        if (!TryParseMonth(match.Groups["mon"].Value, out var month)) return false;

        var year = match.Groups["y"].Success ? ParseInt(match.Groups["y"].Value) : (int?)null;
        return Compose(year, month, ParseInt(match.Groups["d"].Value), now, out value);
    }

    private static bool Compose(int? year, int? month, int? day, DateTimeOffset now, out DateTimeOffset value)
    {
        value = default;
        if (month is null or < 1 or > 12) return false;
        if (day is null or < 1 or > 31) return false;

        var resolved = year is null ? now.ToLocalTime().Year : NormalizeYear(year.Value);
        if (resolved is < 1990 or > 2100) return false;

        if (!TryBuildDate(resolved, month.Value, day.Value, out var date)) return false;

        // 没写年份且已经过去大半年 → 视为下一年（12 月的邮件里写「1 月 5 日」）
        if (year is null && date < now.ToLocalTime().Date.AddDays(-183))
        {
            resolved++;
            if (resolved > 2100 || !TryBuildDate(resolved, month.Value, day.Value, out date)) return false;
        }

        value = new DateTimeOffset(date, TimeSpan.Zero);
        return true;
    }

    private static bool TryBuildDate(int year, int month, int day, out DateTime date)
    {
        date = default;
        if (day > DateTime.DaysInMonth(year, month)) return false;
        date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        return true;
    }

    private static int NormalizeYear(int year)
    {
        if (year >= 1000) return year;
        // 两位年份：90-99 → 1990s，其余 → 2000s
        return year >= 90 ? 1900 + year : 2000 + year;
    }

    private static bool TryParseMonth(string raw, out int month)
    {
        month = 0;
        if (raw.Length < 3) return false;

        month = raw[..3].ToUpperInvariant() switch
        {
            "JAN" => 1,
            "FEB" => 2,
            "MAR" => 3,
            "APR" => 4,
            "MAY" => 5,
            "JUN" => 6,
            "JUL" => 7,
            "AUG" => 8,
            "SEP" => 9,
            "OCT" => 10,
            "NOV" => 11,
            "DEC" => 12,
            _ => 0
        };

        return month > 0;
    }

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static string Clean(string value)
    {
        var text = Regex.Replace(value, @"\s+", " ").Trim();
        return text.Trim(' ', '.', ',', ';', ':', '#', '-', '/', '、', '。', '，');
    }
}
