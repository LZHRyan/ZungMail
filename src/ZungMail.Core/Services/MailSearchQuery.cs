using System.Text;
using System.Text.RegularExpressions;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>业务标识过滤条件；<see cref="Kind"/> 为 null 表示任意类型（<c>ref:</c>）。</summary>
public sealed record MailReferenceFilter(FreightReferenceKind? Kind, string Value)
{
    /// <summary>聚合键形式（大写、仅字母数字），与 <c>message_refs.normalized</c> 对齐。</summary>
    public string Normalized => FreightReference.Normalize(Value);
}

/// <summary>
/// 结构化搜索表达式：把 <c>booking:SO123</c>、<c>has:attachment</c> 这类运算符从查询串中解析出来，
/// 其余文本继续走全文检索。运算符解析失败时保留在自由文本里，不让用户「搜不到」。
/// </summary>
public sealed class MailSearchQuery
{
    private static readonly Regex TokenRegex = new(
        @"(?<key>[\p{L}\p{N}_/]+):(?<value>""[^""]*""|[^\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, FreightReferenceKind?> ReferenceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["job"] = FreightReferenceKind.Job,
        ["业务编号"] = FreightReferenceKind.Job,
        ["业务单号"] = FreightReferenceKind.Job,
        ["so"] = FreightReferenceKind.Booking,
        ["s/o"] = FreightReferenceKind.Booking,
        ["booking"] = FreightReferenceKind.Booking,
        ["bkg"] = FreightReferenceKind.Booking,
        ["订舱号"] = FreightReferenceKind.Booking,
        ["bl"] = FreightReferenceKind.BillOfLading,
        ["b/l"] = FreightReferenceKind.BillOfLading,
        ["hbl"] = FreightReferenceKind.BillOfLading,
        ["mbl"] = FreightReferenceKind.BillOfLading,
        ["提单号"] = FreightReferenceKind.BillOfLading,
        ["ctr"] = FreightReferenceKind.Container,
        ["container"] = FreightReferenceKind.Container,
        ["箱号"] = FreightReferenceKind.Container,
        ["柜号"] = FreightReferenceKind.Container,
        ["seal"] = FreightReferenceKind.Seal,
        ["封号"] = FreightReferenceKind.Seal,
        ["vessel"] = FreightReferenceKind.VesselVoyage,
        ["船名"] = FreightReferenceKind.VesselVoyage,
        ["port"] = FreightReferenceKind.Port,
        ["港口"] = FreightReferenceKind.Port,
        ["inv"] = FreightReferenceKind.Invoice,
        ["invoice"] = FreightReferenceKind.Invoice,
        ["发票号"] = FreightReferenceKind.Invoice,
        ["po"] = FreightReferenceKind.PurchaseOrder,
        ["awb"] = FreightReferenceKind.AirWaybill,
        ["空运单"] = FreightReferenceKind.AirWaybill,
        ["ref"] = null
    };

    /// <summary>去除运算符后的自由文本，交给 FTS5 / LIKE。</summary>
    public string FreeText { get; init; } = "";

    public IReadOnlyList<MailReferenceFilter> References { get; init; } = Array.Empty<MailReferenceFilter>();

    public string? From { get; init; }

    public string? Subject { get; init; }

    public string? FileName { get; init; }

    /// <summary><c>type:si</c> 这类行业邮件类型过滤。</summary>
    public MailType? MailType { get; init; }

    public bool HasAttachment { get; init; }

    /// <summary>true = 只看未读；false = 只看已读；null = 不限。</summary>
    public bool? IsUnread { get; init; }

    public bool? IsStarred { get; init; }

    /// <summary><c>after:2026-09-01</c> / <c>after:7d</c>：只保留该时间点之后（含）的邮件。</summary>
    public DateTimeOffset? After { get; init; }

    /// <summary><c>before:2026-10-01</c> / <c>before:today</c>：只保留该时间点之前的邮件。</summary>
    public DateTimeOffset? Before { get; init; }

    public bool HasFilters => References.Count > 0 || From is not null || Subject is not null
        || FileName is not null || MailType is not null || HasAttachment || IsUnread is not null || IsStarred is not null
        || After is not null || Before is not null;

    /// <summary>是否有任何可执行条件（自由文本或运算符）。</summary>
    public bool IsEmpty => !HasFilters && FreeText.Length == 0;

    public static MailSearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new MailSearchQuery();

        var refs = new List<MailReferenceFilter>();
        var from = default(string);
        var subject = default(string);
        var fileName = default(string);
        var hasAttachment = false;
        bool? isUnread = null;
        bool? isStarred = null;
        MailType? mailType = null;
        DateTimeOffset? after = null;
        DateTimeOffset? before = null;

        // 记录被识别为运算符的区间，剩余部分才是自由文本
        var consumed = new List<(int Start, int Length)>();

        foreach (Match match in TokenRegex.Matches(text))
        {
            var key = match.Groups["key"].Value.Trim();
            var value = match.Groups["value"].Value.Trim('"');
            if (value.Length == 0) continue;

            var handled = true;
            if (ReferenceKeys.TryGetValue(key, out var kind))
            {
                var normalized = FreightReference.Normalize(value);
                if (normalized.Length >= 2) refs.Add(new MailReferenceFilter(kind, normalized));
            }
            else if (key.Equals("from", StringComparison.OrdinalIgnoreCase) || key == "发件人")
            {
                from = value;
            }
            else if (key.Equals("subject", StringComparison.OrdinalIgnoreCase) || key == "主题")
            {
                subject = value;
            }
            else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("fname", StringComparison.OrdinalIgnoreCase)
                     || key == "附件名")
            {
                fileName = value;
            }
            else if (key.Equals("has", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("attachment", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("attachments", StringComparison.OrdinalIgnoreCase)
                    || value == "附件")
                {
                    hasAttachment = true;
                }
                else
                {
                    handled = false;
                }
            }
            else if (key.Equals("is", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("unread", StringComparison.OrdinalIgnoreCase) || value == "未读") isUnread = true;
                else if (value.Equals("read", StringComparison.OrdinalIgnoreCase) || value == "已读") isUnread = false;
                else if (value.Equals("starred", StringComparison.OrdinalIgnoreCase) || value == "星标") isStarred = true;
                else handled = false;
            }
            else if (key.Equals("after", StringComparison.OrdinalIgnoreCase) || key == "之后"
                     || key.Equals("since", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseDate(value, out var parsedAfter)) after = parsedAfter;
                else handled = false;
            }
            else if (key.Equals("before", StringComparison.OrdinalIgnoreCase) || key == "之前"
                     || key.Equals("until", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseDate(value, out var parsedBefore)) before = parsedBefore;
                else handled = false;
            }
            else if (key.Equals("type", StringComparison.OrdinalIgnoreCase) || key == "类型")
            {
                if (MailTypes.TryParseKeyword(value, out var parsedType)) mailType = parsedType;
                else handled = false;
            }
            else
            {
                handled = false;
            }

            if (handled) consumed.Add((match.Index, match.Length));
        }

        return new MailSearchQuery
        {
            FreeText = RemoveRanges(text, consumed),
            References = refs,
            From = from,
            Subject = subject,
            FileName = fileName,
            MailType = mailType,
            HasAttachment = hasAttachment,
            IsUnread = isUnread,
            IsStarred = isStarred,
            After = after,
            Before = before
        };
    }

    /// <summary>
    /// 解析时间运算符的值：支持 <c>yyyy-MM-dd</c>、<c>yyyy-MM</c>、<c>today</c>、
    /// <c>7d</c> / <c>30d</c>（最近 N 天）以及 <c>yyyy-MM-dd HH:mm</c>。
    /// </summary>
    private static bool TryParseDate(string value, out DateTimeOffset result)
    {
        result = default;
        var text = value.Trim();

        if (text.Equals("today", StringComparison.OrdinalIgnoreCase) || text == "今天")
        {
            result = new DateTimeOffset(DateTime.Today, DateTimeOffset.Now.Offset);
            return true;
        }

        if (text.Length > 1 && text[^1] is 'd' or 'D'
            && int.TryParse(text[..^1], out var days) && days is > 0 and <= 3650)
        {
            result = DateTimeOffset.Now.AddDays(-days);
            return true;
        }

        if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var parsed)
            || DateTime.TryParse(text, out parsed))
        {
            result = new DateTimeOffset(parsed, DateTimeOffset.Now.Offset);
            return true;
        }

        return false;
    }

    private static string RemoveRanges(string text, List<(int Start, int Length)> ranges)
    {
        if (ranges.Count == 0) return text.Trim();

        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var (start, length) in ranges.OrderBy(r => r.Start))
        {
            if (start > cursor) builder.Append(text, cursor, start - cursor);
            cursor = Math.Max(cursor, start + length);
        }
        if (cursor < text.Length) builder.Append(text, cursor, text.Length - cursor);

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }
}
