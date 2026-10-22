using System.Globalization;
using System.Text;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>一个占位符的定义：<c>{主题}</c> 这类令牌在套用模板时被替换。</summary>
public sealed record TemplatePlaceholderToken(string Token, string DisplayName, string Hint);

/// <summary>
/// 模板占位符（FR-08）：定义、替换与未填充检测。
/// 内置模板中的 <c>{主题}/{时间}/{地点}</c> 依赖本类才能真正生效。
/// </summary>
public static class TemplatePlaceholder
{
    public static IReadOnlyList<TemplatePlaceholderToken> All { get; } = new[]
    {
        new TemplatePlaceholderToken("{主题}", "主题", "会议 / 事项名称"),
        new TemplatePlaceholderToken("{时间}", "时间", "会议或事项的开始时间"),
        new TemplatePlaceholderToken("{日期}", "日期", "日期，如 2026-10-17"),
        new TemplatePlaceholderToken("{地点}", "地点", "会议地点或线上会议链接"),
        new TemplatePlaceholderToken("{姓名}", "姓名", "收件人称呼"),
        new TemplatePlaceholderToken("{第N周}", "周次", "当前周次，如第 42 周"),
        new TemplatePlaceholderToken("{议题}", "议题", "待讨论的议题")
    };

    /// <summary>按当前时间生成可自动填充的占位符取值。</summary>
    public static IReadOnlyDictionary<string, string> AutoValues(DateTimeOffset now)
    {
        var week = ISOWeek.GetWeekOfYear(now.DateTime);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{时间}"] = now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["{日期}"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["{第N周}"] = $"第 {week} 周"
        };
    }

    /// <summary>
    /// 替换已知占位符；未提供取值（或取值为空白）的令牌保持原样，便于用户发现遗漏。
    /// </summary>
    public static string Resolve(string? text, IReadOnlyDictionary<string, string?>? values)
    {
        if (string.IsNullOrEmpty(text) || values is null || values.Count == 0) return text ?? string.Empty;

        var result = text;
        foreach (var token in All)
        {
            if (!result.Contains(token.Token, StringComparison.Ordinal)) continue;
            if (values.TryGetValue(token.Token, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                result = result.Replace(token.Token, value, StringComparison.Ordinal);
            }
        }
        return result;
    }

    /// <summary>收集文本中仍然存在的占位符（去重，按定义顺序）。</summary>
    public static IReadOnlyList<TemplatePlaceholderToken> Unresolved(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<TemplatePlaceholderToken>();
        return All.Where(t => text.Contains(t.Token, StringComparison.Ordinal)).ToList();
    }

    /// <summary>合并「自动取值 + 用户填写」；用户填写的优先。</summary>
    public static IReadOnlyDictionary<string, string?> MergeValues(
        DateTimeOffset now,
        IReadOnlyDictionary<string, string?>? userValues)
    {
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in AutoValues(now)) merged[pair.Key] = pair.Value;
        if (userValues is not null)
        {
            foreach (var pair in userValues)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value)) merged[pair.Key] = pair.Value;
            }
        }
        return merged;
    }

    /// <summary>生成套用后的模板副本（主题与正文替换，名称 / 分组保持不变）。</summary>
    public static MailTemplate Apply(MailTemplate template, IReadOnlyDictionary<string, string?>? values)
    {
        var clone = template.Clone();
        clone.Subject = Resolve(template.Subject, values);
        clone.Body = Resolve(template.Body, values);
        return clone;
    }

    /// <summary>
    /// 预览用：把未填充的占位符包成高亮标签，让用户一眼看出哪里需要填写。
    /// 标签内部的令牌不做处理，避免破坏 HTML 属性。
    /// </summary>
    public static string HighlightUnresolved(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var pending = Unresolved(html);
        if (pending.Count == 0) return html;

        var sb = new StringBuilder(html.Length + pending.Count * 48);
        var index = 0;
        while (index < html.Length)
        {
            if (html[index] == '<')
            {
                var end = html.IndexOf('>', index);
                if (end < 0)
                {
                    sb.Append(html, index, html.Length - index);
                    break;
                }
                sb.Append(html, index, end - index + 1);
                index = end + 1;
                continue;
            }

            var nextTag = html.IndexOf('<', index);
            var stop = nextTag < 0 ? html.Length : nextTag;
            var text = html.Substring(index, stop - index);
            foreach (var token in pending)
            {
                if (!text.Contains(token.Token, StringComparison.Ordinal)) continue;
                text = text.Replace(token.Token, WrapToken(token.Token), StringComparison.Ordinal);
            }
            sb.Append(text);
            index = stop;
        }
        return sb.ToString();
    }

    private static string WrapToken(string token) =>
        "<span style=\"background:#fff4d6;color:#8a5a00;border:1px dashed #e0b25c;" +
        "border-radius:4px;padding:0 3px;white-space:nowrap\">" + token + "</span>";
}
