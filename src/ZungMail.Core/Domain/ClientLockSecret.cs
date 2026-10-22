using System.Security.Cryptography;

namespace ZungMail.Core.Domain;

/// <summary>
/// 客户端锁定口令的派生与校验（PBKDF2-SHA256，随机盐，固定时间比较）。
/// 派生结果由上层再用 DPAPI 保护后写入设置，避免明文口令落盘。
/// </summary>
public static class ClientLockSecret
{
    /// <summary>口令最短长度。</summary>
    public const int MinPasswordLength = 4;

    private const string Prefix = "pbkdf2-sha256";
    private const int DefaultIterations = 120_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>由明文口令生成校验串（格式：pbkdf2-sha256$迭代次数$盐$哈希，均为 Base64）。</summary>
    public static string Create(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            DefaultIterations,
            HashAlgorithmName.SHA256,
            KeyBytes);

        return $"{Prefix}${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>校验口令是否匹配；校验串为空、格式非法或口令为空时返回 false（不抛异常）。</summary>
    public static bool Verify(string? secret, string? password)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrEmpty(password)) return false;

        var parts = secret.Split('$');
        if (parts.Length != 4) return false;
        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal)) return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations < 1000) return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>口令是否满足最短长度要求。</summary>
    public static bool IsAcceptablePassword(string? password)
        => !string.IsNullOrWhiteSpace(password) && password.Length >= MinPasswordLength;
}
