namespace ZungMail.Core.Services;

/// <summary>
/// 陌生发件人判定（纯逻辑，便于单测）：地址既不在通讯录、也没有更早的来信、又不是本机账户自己时算陌生。
/// <para>
/// 判定只跑在「增量新增的来信」上，且必须在该批邮件写入本地库之前完成：
/// 写入后再查，本批刚进来的行会让同一批发件人的第二封被判成熟人。
/// </para>
/// </summary>
public static class StrangerSenderPolicy
{
    /// <summary>与通讯录 / 仓储一致的地址归一：去空白 + 小写。</summary>
    public static string Normalize(string? address) => (address ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>把一批地址归一成集合（忽略空值），用于自有账户地址与判定结果缓存。</summary>
    public static HashSet<string> NormalizeAll(IEnumerable<string?> addresses)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            var normalized = Normalize(address);
            if (normalized.Length > 0) set.Add(normalized);
        }

        return set;
    }

    /// <summary>
    /// 是否为陌生发件人。<paramref name="knownAddresses"/> 必须已经同时包含
    /// 通讯录地址与历史来信地址；<paramref name="ownAddresses"/> 为本机账户自身地址，命中即视为已知。
    /// </summary>
    public static bool IsStranger(
        string? address,
        IReadOnlySet<string> knownAddresses,
        IReadOnlySet<string>? ownAddresses = null)
    {
        var normalized = Normalize(address);

        // 地址缺失或明显不是邮箱（没有 @）：不打标，避免同步因为脏数据中断
        if (normalized.Length == 0 || !normalized.Contains('@', StringComparison.Ordinal)) return false;

        if (ownAddresses is not null && ownAddresses.Contains(normalized)) return false;

        return !knownAddresses.Contains(normalized);
    }
}
