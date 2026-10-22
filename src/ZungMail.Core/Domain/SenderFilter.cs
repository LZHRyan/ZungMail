namespace ZungMail.Core.Domain;

/// <summary>
/// 本地发件人黑白名单条目。模式为完整地址（<c>boss@example.com</c>）或域名（<c>@example.com</c>），
/// 只影响本地判定，不改写服务器状态（不依赖 IMAP 的 Junk 标记能力）。
/// </summary>
public sealed class SenderFilter
{
    public long Id { get; set; }

    /// <summary>归一化后的模式：小写，域名以 '@' 开头。</summary>
    public string Pattern { get; set; } = "";

    /// <summary>true = 黑名单；false = 白名单。</summary>
    public bool IsBlacklist { get; set; } = true;

    public string Note { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string KindDisplay => IsBlacklist ? "黑名单" : "白名单";

    public string DetailDisplay => string.IsNullOrWhiteSpace(Note)
        ? KindDisplay
        : $"{KindDisplay} · {Note.Trim()}";
}

/// <summary>发件人名单判定结果。白名单优先于黑名单。</summary>
public enum SenderVerdict
{
    /// <summary>未命中任何名单。</summary>
    None = 0,

    /// <summary>命中白名单（永不判为垃圾邮件）。</summary>
    Whitelisted = 1,

    /// <summary>命中黑名单。</summary>
    Blacklisted = 2
}

/// <summary>
/// 黑白名单的归一化与匹配规则（纯函数，便于单元测试）。
/// 支持两种模式：完整地址与域名（以 '@' 开头）；匹配不区分大小写。
/// </summary>
public static class SenderFilterMatch
{
    private static readonly char[] Forbidden =
    {
        ' ', '\t', '\r', '\n', ',', ';', ':', '<', '>', '"', '\'', '(', ')', '[', ']', '\\', '/', '*', '?', '|'
    };

    /// <summary>
    /// 归一化用户输入：去空白与尖括号、去 mailto: 前缀、转小写；
    /// 裸域名自动补 '@'。无法识别时返回 null。
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim().Trim('<', '>').Trim();
        if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) value = value[7..].Trim();
        if (value.Length == 0) return null;
        if (value.IndexOfAny(Forbidden) >= 0) return null;
        if (value.Contains("..", StringComparison.Ordinal) || value.StartsWith('.') || value.EndsWith('.'))
        {
            return null;
        }

        value = value.ToLowerInvariant();

        if (value.StartsWith('@'))
        {
            var domain = value[1..];
            return IsUsableDomain(domain) ? "@" + domain : null;
        }

        var at = value.IndexOf('@');
        if (at < 0)
        {
            // 只输入了域名：按整域匹配处理。
            return IsUsableDomain(value) ? "@" + value : null;
        }

        if (at != value.LastIndexOf('@')) return null;
        var local = value[..at];
        var host = value[(at + 1)..];
        if (local.Length == 0 || !IsUsableDomain(host)) return null;

        return value;
    }

    private static bool IsUsableDomain(string domain)
        => domain.Length >= 3
           && domain.Contains('.', StringComparison.Ordinal)
           && !domain.StartsWith('.')
           && !domain.StartsWith('-')
           && !domain.EndsWith('-');

    /// <summary>从「显示名 &lt;地址&gt;」中提取地址，并转小写。</summary>
    public static string ExtractAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var value = raw.Trim();
        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        if (open >= 0 && close > open) value = value[(open + 1)..close].Trim();
        return value.Trim().ToLowerInvariant();
    }

    /// <summary>判断一条模式是否命中地址。</summary>
    public static bool Matches(string? pattern, string? address)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(address)) return false;

        var normalizedPattern = pattern.Trim().ToLowerInvariant();
        var normalizedAddress = ExtractAddress(address);
        var at = normalizedAddress.LastIndexOf('@');
        if (at < 0) return false;

        if (normalizedPattern.StartsWith('@'))
        {
            return string.Equals(normalizedAddress[(at + 1)..], normalizedPattern[1..], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(normalizedAddress, normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按名单顺序判定：先命中白名单即返回白名单，否则黑名单优先于未命中。</summary>
    public static SenderVerdict Classify(IEnumerable<SenderFilter>? filters, string? address)
    {
        if (filters is null || string.IsNullOrWhiteSpace(address)) return SenderVerdict.None;

        var blacklisted = false;
        foreach (var filter in filters)
        {
            if (!Matches(filter.Pattern, address)) continue;
            if (!filter.IsBlacklist) return SenderVerdict.Whitelisted;
            blacklisted = true;
        }

        return blacklisted ? SenderVerdict.Blacklisted : SenderVerdict.None;
    }
}
