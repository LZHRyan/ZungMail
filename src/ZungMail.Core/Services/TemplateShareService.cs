using System.Text.Json;
using System.Text.Json.Serialization;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>分享文件里的一条模板（不含 Id / 内置标记，导入方各自生成）。</summary>
public sealed class TemplateShareEntry
{
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";

    /// <summary>true 表示这条是快速文本，导入后仍进快速文本 Tab。</summary>
    public bool IsQuickText { get; set; }
}

/// <summary>模板分享文件的外层结构（带格式标识与版本号，方便以后扩展）。</summary>
public sealed class TemplateShareDocument
{
    public const string FormatId = "zungmail.templates";

    /// <summary>改名前（MailClient）导出的文件格式标识；导入时一并接受，导出仍写新标识。</summary>
    public const string LegacyFormatId = "mailclient.templates";

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = TemplateShareService.CurrentVersion;
    public string App { get; set; } = "ZungMail";
    public string ExportedUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public List<TemplateShareEntry> Templates { get; set; } = new();
}

/// <summary>解析结果：失败时给出可直接展示的原因。</summary>
public sealed record TemplateShareParseResult(
    bool Ok,
    string Message,
    IReadOnlyList<TemplateShareEntry> Entries,
    int TemplateCount,
    int QuickTextCount)
{
    public static TemplateShareParseResult Fail(string message) =>
        new(false, message, Array.Empty<TemplateShareEntry>(), 0, 0);
}

/// <summary>
/// 模板 / 快速文本的分享：导出为带版本号的 JSON，导入时校验格式、重命名冲突项。
/// 纯逻辑、不碰文件系统与 UI，便于单测。
/// </summary>
public static class TemplateShareService
{
    /// <summary>当前分享文件版本；结构变更时递增，导入端拒绝比自己新的版本。</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>把模板导出成分享文件内容。</summary>
    public static string Serialize(IEnumerable<MailTemplate> templates)
    {
        var document = new TemplateShareDocument
        {
            Templates = templates
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .Select(t => new TemplateShareEntry
                {
                    Name = t.Name.Trim(),
                    Subject = t.Subject ?? "",
                    Body = t.Body ?? "",
                    Description = t.Description ?? "",
                    Category = string.IsNullOrWhiteSpace(t.Category) ? MailTemplate.DefaultCategory : t.Category,
                    IsQuickText = t.IsQuickText
                })
                .ToList()
        };

        return JsonSerializer.Serialize(document, WriteOptions);
    }

    /// <summary>解析分享文件；格式不对 / 版本过新 / 没有条目都会给出中文原因。</summary>
    public static TemplateShareParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return TemplateShareParseResult.Fail("文件是空的。");

        TemplateShareDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<TemplateShareDocument>(json, ReadOptions);
        }
        catch (JsonException)
        {
            return TemplateShareParseResult.Fail("不是有效的 JSON 文件。");
        }

        if (document is null) return TemplateShareParseResult.Fail("文件内容无法识别。");

        if (!IsKnownFormat(document.Format))
        {
            return TemplateShareParseResult.Fail("这不是 ZungMail 的模板文件。");
        }

        if (document.Version > CurrentVersion)
        {
            return TemplateShareParseResult.Fail(
                $"文件版本（v{document.Version}）比当前客户端新，请先升级客户端。");
        }

        var entries = (document.Templates ?? new List<TemplateShareEntry>())
            .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Name))
            .ToList();

        if (entries.Count == 0) return TemplateShareParseResult.Fail("文件里没有可导入的模板。");

        return new TemplateShareParseResult(
            true,
            $"共 {entries.Count} 条",
            entries,
            entries.Count(e => !e.IsQuickText),
            entries.Count(e => e.IsQuickText));
    }

    /// <summary>
    /// 认新标识，也认改名前的 <see cref="TemplateShareDocument.LegacyFormatId"/>：
    /// 同事手里可能还留着旧版导出的模板文件。
    /// </summary>
    private static bool IsKnownFormat(string? format)
        => string.Equals(format, TemplateShareDocument.FormatId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(format, TemplateShareDocument.LegacyFormatId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把分享条目落成可保存的模板：重新生成 Id、去掉内置标记、同名自动加序号（避免覆盖同事的同名模板）。
    /// </summary>
    public static IReadOnlyList<MailTemplate> Materialize(
        IEnumerable<TemplateShareEntry> entries,
        IEnumerable<MailTemplate> existing,
        DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        var usedNames = new HashSet<string>(
            existing.Select(t => t.Name?.Trim() ?? "").Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var result = new List<MailTemplate>();
        foreach (var entry in entries)
        {
            var isQuick = entry.IsQuickText;
            var name = MakeUniqueName(entry.Name?.Trim() ?? "", usedNames);
            usedNames.Add(name);

            result.Add(new MailTemplate
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Subject = entry.Subject ?? "",
                Body = entry.Body ?? "",
                Description = entry.Description ?? "",
                Category = string.IsNullOrWhiteSpace(entry.Category)
                    ? (isQuick ? "常用语" : MailTemplate.DefaultCategory)
                    : entry.Category,
                IsBuiltIn = false,
                IsQuickText = isQuick,
                UpdatedUtc = stamp
            });
        }

        return result;
    }

    private static string MakeUniqueName(string name, HashSet<string> used)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "未命名模板" : name;
        if (!used.Contains(baseName)) return baseName;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!used.Contains(candidate)) return candidate;
        }

        return $"{baseName} {Guid.NewGuid().ToString("N")[..4]}";
    }
}
