namespace ZungMail.Core.Services;

/// <summary>
/// 会话归并键推导（FR-05）：优先使用 References 链的根节点，
/// 其次使用 Message-ID，最后退回到主题归一化结果。
/// </summary>
public static class ThreadKeyBuilder
{
    public static string Build(string? messageId, IEnumerable<string>? references, string? subject)
    {
        var roots = (references ?? Enumerable.Empty<string>())
            .Select(Clean)
            .Where(r => r.Length > 0)
            .ToList();

        if (roots.Count > 0) return roots[0];

        var mid = Clean(messageId);
        if (mid.Length > 0) return mid;

        var normalized = SubjectNormalizer.Normalize(subject);
        return normalized.Length > 0 ? "s:" + normalized : string.Empty;
    }

    /// <summary>去掉尖括号、空白并小写化。</summary>
    public static string Clean(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return string.Empty;
        return messageId.Trim().Trim('<', '>').Trim().ToLowerInvariant();
    }
}
