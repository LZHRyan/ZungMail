using System.Globalization;

namespace ZungMail.Update.Protocol;

/// <summary>
/// 升级版本号（语义化版本的宽松实现）：支持 <c>1.2.3</c>、<c>1.2.3.4</c>
/// 与预发布后缀 <c>1.2.3-beta.1</c>，忽略构建元数据 <c>+sha</c>。
/// 服务端筛选与客户端比较共用本类型，避免两边规则漂移。
/// </summary>
public readonly struct UpdateVersion : IComparable<UpdateVersion>, IEquatable<UpdateVersion>
{
    private readonly int[] _numbers;
    private readonly string[] _prerelease;

    private UpdateVersion(int[] numbers, string[] prerelease, string original)
    {
        _numbers = numbers;
        _prerelease = prerelease;
        Original = original;
    }

    /// <summary>解析时使用的原始文本（用于日志与界面展示）。</summary>
    public string Original { get; }

    public bool IsPrerelease => _prerelease.Length > 0;

    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        // 构建元数据不参与比较
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];

        var prerelease = Array.Empty<string>();
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            var suffix = value[(dash + 1)..];
            value = value[..dash];
            if (suffix.Length == 0) return false;
            prerelease = suffix.Split('.');
            if (prerelease.Any(part => part.Length == 0)) return false;
            // 只接受 ASCII 字母数字与连字符，避免奇怪的输入进入比较
            foreach (var part in prerelease)
            {
                if (!part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')) return false;
            }
        }

        var segments = value.Split('.');
        // 发布版本必须是三段数字（可带第四段构建号），避免「1.1」这类含糊写法进入比较
        if (segments.Length < 3 || segments.Length > 4) return false;

        var numbers = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return false;
            numbers[i] = number;
        }

        version = new UpdateVersion(numbers, prerelease, text.Trim());
        return true;
    }

    public static UpdateVersion Parse(string text)
        => TryParse(text, out var version)
            ? version
            : throw new FormatException($"版本号「{text}」不是合法格式（应形如 1.2.3 或 1.2.3-beta.1）。");

    public int CompareTo(UpdateVersion other)
    {
        var length = Math.Max(_numbers.Length, other._numbers.Length);
        for (var i = 0; i < length; i++)
        {
            var left = i < _numbers.Length ? _numbers[i] : 0;
            var right = i < other._numbers.Length ? other._numbers[i] : 0;
            if (left != right) return left.CompareTo(right);
        }

        // 同号：预发布低于正式版；两边都是预发布时逐段比较
        if (_prerelease.Length == 0 && other._prerelease.Length == 0) return 0;
        if (_prerelease.Length == 0) return 1;
        if (other._prerelease.Length == 0) return -1;

        var count = Math.Max(_prerelease.Length, other._prerelease.Length);
        for (var i = 0; i < count; i++)
        {
            if (i >= _prerelease.Length) return -1;
            if (i >= other._prerelease.Length) return 1;

            var left = _prerelease[i];
            var right = other._prerelease[i];
            var leftNumeric = long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            int result;
            if (leftNumeric && rightNumeric) result = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric) result = -1;              // 数字标识小于字母标识
            else if (rightNumeric) result = 1;
            else result = string.CompareOrdinal(left, right);

            if (result != 0) return result;
        }

        return 0;
    }

    public bool Equals(UpdateVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is UpdateVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var number in _numbers) hash.Add(number);
        foreach (var part in _prerelease) hash.Add(part, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>规范化文本：补齐到三段数字，便于状态栏与设置页展示。</summary>
    public override string ToString()
    {
        var numbers = _numbers.Length == 0 ? "0.0.0" : string.Join('.', _numbers);
        return _prerelease.Length == 0 ? numbers : numbers + "-" + string.Join('.', _prerelease);
    }

    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;
}
