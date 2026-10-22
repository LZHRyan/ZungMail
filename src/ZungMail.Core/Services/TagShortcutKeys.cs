namespace ZungMail.Core.Services;

/// <summary>
/// 标签快捷键只允许「单键」：数字 1-9、字母 A-Z、功能键 F1-F12，不带任何修饰键。
/// 修饰键组合留给应用自身的快捷键（Ctrl+N 写邮件、F5 刷新等），避免互相抢占。
/// </summary>
public static class TagShortcutKeys
{
    /// <summary>可绑定的全部单键，按「数字 → 字母 → 功能键」排列，供下拉框使用。</summary>
    public static readonly string[] All = Build();

    /// <summary>
    /// 被应用自身占用的键：即使出现在 <see cref="All"/> 里也不允许绑定。
    /// F5 是刷新、F1 预留帮助；其余功能键可用。
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "F1",
        "F5"
    };

    /// <summary>规范化键位文本：去空白、字母转大写；空值返回 null。</summary>
    public static string? Normalize(string? key)
    {
        var text = (key ?? "").Trim().ToUpperInvariant();
        return text.Length == 0 ? null : text;
    }

    /// <summary>该键位是否可绑定（在允许集合内且未被应用占用）。</summary>
    public static bool IsAllowed(string? key)
    {
        var normalized = Normalize(key);
        if (normalized is null) return false;
        if (Reserved.Contains(normalized)) return false;
        return Array.Exists(All, item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>校验并返回规范化键位；不合法时给出可直接展示的原因。</summary>
    public static bool TryNormalize(string? key, out string? normalized, out string? error)
    {
        var text = Normalize(key);
        normalized = text;
        error = null;

        if (text is null) return true; // 未绑定是允许的

        if (Reserved.Contains(text))
        {
            error = $"{text} 已被客户端占用，请换一个键。";
            return false;
        }

        if (!Array.Exists(All, item => string.Equals(item, text, StringComparison.OrdinalIgnoreCase)))
        {
            error = "快捷键只能是 1-9、A-Z 或 F1-F12 这样的单键（不带 Ctrl / Alt / Shift / Win）。";
            return false;
        }

        return true;
    }

    private static string[] Build()
    {
        var keys = new List<string>(9 + 26 + 11);
        for (var i = 1; i <= 9; i++) keys.Add(i.ToString());
        for (var c = 'A'; c <= 'Z'; c++) keys.Add(c.ToString());
        for (var i = 2; i <= 12; i++)
        {
            if (i == 5) continue; // F5 是刷新，不给标签用
            keys.Add("F" + i);
        }

        return keys.ToArray();
    }
}
