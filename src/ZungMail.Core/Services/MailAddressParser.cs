using System.Text;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>RFC 5322 简式地址解析：「显示名 &lt;addr@host&gt;」「addr@host」「"含逗号, 的名字" &lt;a@b&gt;」。</summary>
public static class MailAddressParser
{
    public static EmailAddress Parse(string? raw)
    {
        var list = ParseList(raw);
        return list.Count > 0 ? list[0] : new EmailAddress();
    }

    public static List<EmailAddress> ParseList(string? raw)
    {
        var result = new List<EmailAddress>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var token in SplitTopLevel(raw))
        {
            var address = ParseSingle(token);
            if (address is not null && !string.IsNullOrWhiteSpace(address.Address))
                result.Add(address);
        }
        return result;
    }

    public static List<EmailAddress> ParseList(IEnumerable<string>? raws)
    {
        var result = new List<EmailAddress>();
        if (raws is null) return result;
        foreach (var raw in raws)
        {
            foreach (var token in SplitTopLevel(raw ?? string.Empty))
            {
                var address = ParseSingle(token);
                if (address is not null && !string.IsNullOrWhiteSpace(address.Address))
                    result.Add(address);
            }
        }
        return result;
    }

    /// <summary>按逗号/分号切分，忽略引号与尖括号内部的定界符。</summary>
    private static IEnumerable<string> SplitTopLevel(string raw)
    {
        var sb = new StringBuilder();
        var inQuote = false;
        var angleDepth = 0;
        foreach (var ch in raw)
        {
            switch (ch)
            {
                case '"':
                    inQuote = !inQuote;
                    sb.Append(ch);
                    break;
                case '<' when !inQuote:
                    angleDepth++;
                    sb.Append(ch);
                    break;
                case '>' when !inQuote:
                    if (angleDepth > 0) angleDepth--;
                    sb.Append(ch);
                    break;
                case ',' or ';' when !inQuote && angleDepth == 0:
                    yield return sb.ToString();
                    sb.Clear();
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static EmailAddress? ParseSingle(string token)
    {
        var s = token.Trim();
        if (s.Length == 0) return null;

        var lt = s.IndexOf('<');
        if (lt >= 0)
        {
            var gt = s.IndexOf('>', lt + 1);
            var addr = gt > lt ? s[(lt + 1)..gt] : s[(lt + 1)..];
            var name = s[..lt].Trim().Trim('"').Trim();
            return new EmailAddress(addr.Trim(), name);
        }

        // 无尖括号：回退为「整串即地址」，此时若含空白则取最后一段像地址的部分
        var trimmed = s.Trim('"').Trim();
        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace > 0 && trimmed.Contains('@'))
        {
            var candidate = trimmed[(lastSpace + 1)..];
            if (candidate.Contains('@')) return new EmailAddress(candidate, trimmed[..lastSpace].Trim());
        }
        return new EmailAddress(trimmed, "");
    }
}
