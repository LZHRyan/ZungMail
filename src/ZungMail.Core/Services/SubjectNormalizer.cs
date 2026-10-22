using System.Text.RegularExpressions;

namespace ZungMail.Core.Services;

/// <summary>主题前缀归一化：剥离 Re:/Fwd:/回复: 等前缀，用于会话归并与「无主题」判定。</summary>
public static partial class SubjectNormalizer
{
    // 常见邮件客户端与中文本地化前缀
    private static readonly string[] PrefixTokens =
    {
        "re", "fw", "fwd", "aw", "sv", "vs", "答复", "回复", "回覆", "回信", "转发", "转寄", "转發"
    };

    /// <summary>反复剥离前缀，直至不再变化。保留原始大小写与内容。</summary>
    public static string StripPrefixes(string? subject)
    {
        var s = (subject ?? string.Empty).Trim();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var token in PrefixTokens)
            {
                var m = PrefixRegex(token).Match(s);
                if (m.Success && m.Index == 0)
                {
                    s = s[m.Length..].TrimStart(' ', ':', '：', '-', '_', '\u3000');
                    changed = true;
                    break;
                }
            }
        }
        return s.Trim();
    }

    /// <summary>归一化（剥离前缀 + 小写），用于会话回退键。</summary>
    public static string Normalize(string? subject) => StripPrefixes(subject).ToLowerInvariant();

    private static readonly Dictionary<string, Regex> Cache = new();

    private static Regex PrefixRegex(string token)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(token, out var cached)) return cached;
            // 形如： re:  re[2]:  re  :  转发:  答复：
            var pattern = "^" + Regex.Escape(token) + @"\s*(\[\d+\])?\s*[:：]";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Cache[token] = regex;
            return regex;
        }
    }
}
