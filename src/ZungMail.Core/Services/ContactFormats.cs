using System.Text;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>联系人文件格式。</summary>
public enum ContactFileFormat
{
    VCard = 0,
    Csv = 1
}

/// <summary>解析出的联系人条目（导入前的最小形态）。</summary>
public sealed record ContactEntry(string Email, string Name)
{
    public Contact ToContact() => new() { Email = Email, Name = Name };
}

/// <summary>
/// 联系人文件读写（vCard 3.0 与 CSV）。解析与生成都是纯函数，便于单元测试。
/// vCard 支持折行展开、QUOTED-PRINTABLE 与 FN/N 兜底；CSV 按 RFC 4180 处理引号，
/// 表头自动识别「邮箱 / E-mail」「姓名 / Name」，无表头时按「邮箱,姓名」列序。
/// </summary>
public static class ContactFormats
{
    public static ContactFileFormat DetectFormat(string? path)
        => string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase)
            ? ContactFileFormat.Csv
            : ContactFileFormat.VCard;

    public static IReadOnlyList<ContactEntry> Parse(string? content, ContactFileFormat format)
    {
        if (string.IsNullOrWhiteSpace(content)) return Array.Empty<ContactEntry>();

        var entries = format == ContactFileFormat.Csv ? ParseCsv(content) : ParseVCards(content);

        // 同一文件内按邮箱去重（保留先出现的名字，缺名字时由后者补齐）。
        var map = new Dictionary<string, ContactEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!map.TryGetValue(entry.Email, out var existing))
            {
                map[entry.Email] = entry;
                continue;
            }

            if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(entry.Name))
            {
                map[entry.Email] = existing with { Name = entry.Name };
            }
        }

        return map.Values.ToList();
    }

    public static string Write(IReadOnlyList<Contact> contacts, ContactFileFormat format)
        => format == ContactFileFormat.Csv ? WriteCsv(contacts) : WriteVCards(contacts);

    // ---------------- vCard ----------------

    private static List<ContactEntry> ParseVCards(string content)
    {
        var result = new List<ContactEntry>();
        foreach (var block in SplitVCards(content))
        {
            var formattedName = "";
            var structuredName = "";
            string? email = null;

            foreach (var line in block)
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;

                var rawKey = line[..colon];
                var value = line[(colon + 1)..];
                var key = rawKey.Split(';')[0].Trim().ToUpperInvariant();

                // 标明编码的按声明解码；未标明的按「=XX=XX」形态兜底识别，兼容手工/旧版导出。
                if (rawKey.Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)
                    || LooksQuotedPrintable(value))
                {
                    value = DecodeQuotedPrintable(value);
                }

                value = UnescapeVCard(value.Trim());

                switch (key)
                {
                    case "FN" when formattedName.Length == 0:
                        formattedName = value;
                        break;
                    case "N" when structuredName.Length == 0:
                        structuredName = value;
                        break;
                    case "EMAIL" when email is null:
                        var candidate = NormalizeEmail(value);
                        if (candidate is not null) email = candidate;
                        break;
                }
            }

            if (email is null) continue;

            // FN 优先；否则用 N 的结构化字段（姓;名;...）拼一个可读名字。
            var name = formattedName.Length > 0
                ? formattedName
                : string.Join(' ', structuredName.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            result.Add(new ContactEntry(email, name.Trim()));
        }

        return result;
    }

    /// <summary>展开折行并按 BEGIN/END 切块，只保留卡片内部的行。</summary>
    private static IEnumerable<List<string>> SplitVCards(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var unfolded = new List<string>();
        foreach (var line in normalized.Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && unfolded.Count > 0)
            {
                unfolded[^1] += line.TrimStart();
            }
            else
            {
                unfolded.Add(line);
            }
        }

        List<string>? current = null;
        foreach (var line in unfolded)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                current = new List<string>();
                continue;
            }

            if (trimmed.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (current is { Count: > 0 }) yield return current;
                current = null;
                continue;
            }

            current?.Add(trimmed);
        }

        // 容错：缺少 END:VCARD 但内容完整的卡片也要导入。
        if (current is { Count: > 0 }) yield return current;
    }

    private static string WriteVCards(IReadOnlyList<Contact> contacts)
    {
        var builder = new StringBuilder();
        foreach (var contact in contacts)
        {
            var name = EscapeVCard(string.IsNullOrWhiteSpace(contact.Name) ? contact.Email : contact.Name);
            builder.Append("BEGIN:VCARD\r\n");
            builder.Append("VERSION:3.0\r\n");
            builder.Append("N:").Append(name).Append(";;;;\r\n");
            builder.Append("FN:").Append(name).Append("\r\n");
            builder.Append("EMAIL;TYPE=INTERNET:").Append(contact.Email).Append("\r\n");
            builder.Append("END:VCARD\r\n");
        }

        return builder.ToString();
    }

    private static string EscapeVCard(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace(";", "\\;")
            .Replace(",", "\\,")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n");

    private static string UnescapeVCard(string value)
    {
        if (!value.Contains('\\')) return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                builder.Append(next is 'n' or 'N' ? '\n' : next);
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    /// <summary>值形如「=E6=9D=8E...」时按 QUOTED-PRINTABLE 兜底解码。</summary>
    private static bool LooksQuotedPrintable(string value)
    {
        if (value.Length < 3 || value[0] != '=') return false;

        for (var i = 0; i + 2 < value.Length; i += 3)
        {
            if (value[i] != '=' || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return false;
        }

        return true;
    }

    private static string DecodeQuotedPrintable(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '=' && i + 2 < value.Length
                && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }

            if (c == '=' && i == value.Length - 1) continue;   // 软换行残留
            bytes.Add((byte)c);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    // ---------------- CSV ----------------

    private static List<ContactEntry> ParseCsv(string content)
    {
        var rows = SplitCsv(content);
        if (rows.Count == 0) return new List<ContactEntry>();

        var emailIndex = 0;
        var nameIndex = 1;
        var startRow = 0;

        for (var i = 0; i < rows[0].Count; i++)
        {
            var cell = rows[0][i].Trim();
            if (LooksLikeEmailHeader(cell)) emailIndex = i;
            else if (LooksLikeNameHeader(cell)) nameIndex = i;
            else continue;

            startRow = 1;
        }

        var result = new List<ContactEntry>();
        for (var r = startRow; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 0) continue;

            var email = NormalizeEmail(emailIndex < row.Count ? row[emailIndex] : null);
            if (email is null) continue;

            var name = nameIndex >= 0 && nameIndex < row.Count ? row[nameIndex].Trim() : "";
            result.Add(new ContactEntry(email, name));
        }

        return result;
    }

    private static bool LooksLikeEmailHeader(string cell)
        => cell.Contains("邮箱", StringComparison.Ordinal)
           || cell.Contains("邮件地址", StringComparison.Ordinal)
           || cell.Equals("email", StringComparison.OrdinalIgnoreCase)
           || cell.Equals("e-mail", StringComparison.OrdinalIgnoreCase)
           || cell.Equals("mail", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeNameHeader(string cell)
        => cell.Contains("姓名", StringComparison.Ordinal)
           || cell.Contains("名字", StringComparison.Ordinal)
           || cell.Contains("显示名", StringComparison.Ordinal)
           || cell.Equals("name", StringComparison.OrdinalIgnoreCase)
           || cell.Equals("display name", StringComparison.OrdinalIgnoreCase);

    /// <summary>RFC 4180 风格拆分：支持引号包裹、双引号转义与字段内换行。</summary>
    private static List<List<string>> SplitCsv(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var hasContent = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            switch (c)
            {
                case '"':
                    if (inQuotes && i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                    hasContent = true;
                    break;

                case ',' when !inQuotes:
                    row.Add(field.ToString());
                    field.Clear();
                    hasContent = true;
                    break;

                case '\r' when !inQuotes:
                    break;

                case '\n' when !inQuotes:
                    row.Add(field.ToString());
                    field.Clear();
                    if (hasContent || row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                    hasContent = false;
                    break;

                default:
                    field.Append(c);
                    hasContent = true;
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (hasContent || row.Any(cell => cell.Length > 0)) rows.Add(row);
        }

        return rows;
    }

    private static string WriteCsv(IReadOnlyList<Contact> contacts)
    {
        var builder = new StringBuilder();
        builder.Append("姓名,邮箱,使用次数,白名单\r\n");
        foreach (var contact in contacts)
        {
            builder.Append(EscapeCsv(contact.Name)).Append(',')
                .Append(EscapeCsv(contact.Email)).Append(',')
                .Append(contact.UseCount).Append(',')
                .Append(contact.IsWhitelisted ? "是" : "否").Append("\r\n");
        }

        return builder.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        if (text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return text;

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    // ---------------- 公共 ----------------

    private static string? NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var email = value.Trim();
        if (email.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) email = email[7..].Trim();
        email = email.Trim('<', '>').Trim();
        if (email.Length < 3 || email.Contains(' ') || !email.Contains('@', StringComparison.Ordinal)) return null;

        return email.ToLowerInvariant();
    }
}
