using System.Text;

namespace ZungMail.Core.Services;

/// <summary>
/// FTS5 文本归一化器。
/// SQLite 的 unicode61 分词器不会切分中文，导致中文子串检索失效；
/// 这里把 CJK 逐字切分为独立 token，拉丁词保持整词，索引与查询使用同一套规则，
/// 从而使中文、英文、中英混排都能命中（FR-06）。
/// </summary>
public static class FtsTextNormalizer
{
    /// <summary>单次查询最多使用的 token 数，避免病态查询。</summary>
    public const int MaxQueryTokens = 16;

    /// <summary>索引侧：归一化为以空格分隔的 token 串。</summary>
    public static string Normalize(string? text)
    {
        var tokens = Tokenize(text);
        return tokens.Count == 0 ? string.Empty : string.Join(' ', tokens);
    }

    /// <summary>切分 token：CJK 逐字，拉丁/数字连续成词，其余为分隔符。</summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;

        var buffer = new StringBuilder();
        void Flush()
        {
            if (buffer.Length == 0) return;
            result.Add(buffer.ToString().ToLowerInvariant());
            buffer.Clear();
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                Flush();
                result.Add(char.ToLowerInvariant(ch).ToString());
            }
            else if (char.IsLetterOrDigit(ch))
            {
                buffer.Append(ch);
            }
            else
            {
                Flush();
            }
        }
        Flush();
        return result;
    }

    /// <summary>查询侧：构造 FTS5 MATCH 表达式，形如 <c>a* AND b*</c>；无有效 token 时返回空串。</summary>
    public static string BuildMatchExpression(string? query)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0) return string.Empty;

        var distinct = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Length == 0 || token.Length > 64) continue;
            if (!distinct.Contains(token)) distinct.Add(token);
            if (distinct.Count >= MaxQueryTokens) break;
        }
        if (distinct.Count == 0) return string.Empty;

        // token 已在切分阶段过滤为字母/数字/CJK，不含 FTS5 语法字符，可安全拼接
        return string.Join(" AND ", distinct.Select(t => t + "*"));
    }

    /// <summary>把查询串包成 LIKE 模式，用于 FTS5 不可用时的回退。</summary>
    public static string ToLikePattern(string? query) => "%" + (query ?? string.Empty).Trim() + "%";

    public static bool IsCjk(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) ||
        (ch >= 0x3400 && ch <= 0x4DBF) ||
        (ch >= 0xF900 && ch <= 0xFAFF) ||
        (ch >= 0x3040 && ch <= 0x30FF) ||
        (ch >= 0xAC00 && ch <= 0xD7AF);
}
