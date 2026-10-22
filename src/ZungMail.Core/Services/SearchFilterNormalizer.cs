using System.Text.RegularExpressions;

namespace ZungMail.Core.Services;

/// <summary>
/// 高级筛选输入框的取值整理：用户往往是从邮件正文/邮件头里直接复制粘贴，
/// 粘进来的常见形态是 <c>王小明 &lt;abc@qq.com&gt;</c>、<c>"abc@qq.com"</c>、带换行的主题片段，
/// 直接拼进 <c>from:</c> / <c>subject:"…"</c> 会搜不到甚至截断语法，这里先统一收拾干净。
/// </summary>
public static class SearchFilterNormalizer
{
    private static readonly Regex AngleAddress = new("<(?<value>[^<>]+)>", RegexOptions.Compiled);

    private static readonly Regex BareAddress = new(
        @"[\p{L}\p{N}._%+-]+@[\p{L}\p{N}.-]+\.[\p{L}]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 发件人条件：优先取尖括号里的地址，其次取整串里第一个邮箱，最后才回退成用户输入的原文
    /// （原文可以是域名，例如 <c>qq.com</c> 或 <c>@qq.com</c>）。
    /// </summary>
    public static string From(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;

        var angle = AngleAddress.Match(value);
        if (angle.Success)
        {
            var inner = angle.Groups["value"].Value.Trim().Trim('"', '\'', ' ');
            if (inner.Length > 0) return inner;
        }

        var bare = BareAddress.Match(value);
        if (bare.Success) return bare.Value;

        return Whitespace.Replace(value, " ").Trim('"', '\'', ' ');
    }

    /// <summary>
    /// 关键词条件：去掉会截断 <c>subject:"…"</c> 语法的双引号，把换行/连续空白压成一个空格。
    /// </summary>
    public static string Keyword(string? raw)
    {
        var value = (raw ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal);
        return Whitespace.Replace(value, " ").Trim();
    }

    /// <summary>拼进查询串时，含空白的值必须加引号，否则会被当成两个词。</summary>
    public static string QuoteIfNeeded(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        return text.Any(char.IsWhiteSpace) ? "\"" + text + "\"" : text;
    }
}
