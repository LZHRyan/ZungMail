using System.Globalization;

namespace ZungMail.Core.Domain;

/// <summary>面向界面的纯格式化工具（无副作用，便于单测）。</summary>
public static class DisplayFormat
{
    private static readonly string[] Palette =
    {
        "#2563eb", "#0d9488", "#d97706", "#7c3aed", "#64748b", "#db2777", "#0891b2", "#65a30d"
    };

    /// <summary>取名称或邮箱的首字符作为头像文字。</summary>
    public static string Initial(string? nameOrEmail)
    {
        if (string.IsNullOrWhiteSpace(nameOrEmail)) return "?";
        var s = nameOrEmail.Trim();
        // 中文等宽字符直接取首个字符
        return s.Length >= 1 ? s[..1].ToUpperInvariant() : "?";
    }

    /// <summary>由任意键稳定映射到调色板中的颜色。</summary>
    public static string ColorFor(string? key)
    {
        if (string.IsNullOrEmpty(key)) return Palette[0];
        unchecked
        {
            var hash = 17;
            foreach (var ch in key) hash = hash * 31 + ch;
            var idx = Math.Abs(hash) % Palette.Length;
            return Palette[idx];
        }
    }

    /// <summary>列表中的时间显示：今天显示时间，昨天显示「昨天」，更早显示日期。</summary>
    public static string RelativeDate(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        var day = local.Date;
        if (day == today) return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (day == today.AddDays(-1)) return "昨天";
        if (day.Year == today.Year) return local.ToString("MM-dd", CultureInfo.InvariantCulture);
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>完整时间显示。</summary>
    public static string FullDate(DateTimeOffset value)
        => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>文件大小显示。</summary>
    public static string FileSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{size:0} {units[unit]}"
            : $"{size:0.#} {units[unit]}";
    }

    /// <summary>把任意文本压成列表预览（去换行、限长）。</summary>
    public static string Preview(string? text, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        s = s.Trim();
        return s.Length <= maxLength ? s : s[..maxLength] + "…";
    }

    /// <summary>邮件地址的域名部分。</summary>
    public static string DomainOf(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "";
        var at = email.LastIndexOf('@');
        return at >= 0 && at < email.Length - 1 ? email[(at + 1)..].Trim().ToLowerInvariant() : "";
    }
}
