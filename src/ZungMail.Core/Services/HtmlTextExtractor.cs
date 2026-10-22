using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ZungMail.Core.Services;

/// <summary>HTML ↔ 纯文本转换：用于生成列表预览、纯文本回退与写信时的纯文本收录。</summary>
public static partial class HtmlTextExtractor
{
    /// <summary>提取可读纯文本（去脚本样式、块级换行、实体解码）。</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var s = html;
        s = ScriptStyleRegex().Replace(s, " ");
        s = BlockBreakRegex().Replace(s, "\n");
        s = ListItemRegex().Replace(s, "\n• ");
        s = TagRegex().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = s.Replace('\u00a0', ' ');
        s = new string(s.Select(c => c == '\t' || c == '\r' ? ' ' : c).ToArray());

        // 压缩连续空行与行内多空格
        var lines = s.Split('\n')
            .Select(l => CollapseSpacesRegex().Replace(l, " ").Trim());
        var sb = new StringBuilder();
        var blank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (sb.Length > 0 && !blank) { sb.Append('\n'); blank = true; }
                continue;
            }
            if (sb.Length > 0 && !blank) sb.Append('\n');
            sb.Append(line);
            blank = false;
        }
        return sb.ToString().Trim();
    }

    /// <summary>生成列表预览串。</summary>
    public static string BuildPreview(string? htmlOrText, int maxLength = 160, bool isHtml = true)
    {
        var text = isHtml ? ToPlainText(htmlOrText) : (htmlOrText ?? string.Empty);
        return Domain.DisplayFormat.Preview(text, maxLength);
    }

    /// <summary>纯文本 → 简单 HTML（段落包裹，转义特殊字符）。</summary>
    public static string ToHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "<p></p>";
        var paragraphs = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        foreach (var p in paragraphs)
        {
            var escaped = WebUtility.HtmlEncode(p);
            sb.Append("<p>").Append(escaped.Length == 0 ? "<br/>" : escaped).Append("</p>");
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/table|/h[1-6]|/li)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \u3000]{2,}")]
    private static partial Regex CollapseSpacesRegex();
}
