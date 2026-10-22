using System.Text.Json;

namespace ZungMail.Core.Services;

/// <summary>分享文件里的一条账户签名。用邮箱地址而不是账户 id 作为身份，便于换机器 / 换同事导入。</summary>
public sealed class SignatureShareEntry
{
    /// <summary>账户邮箱（导入时按它匹配本地账户）。</summary>
    public string Account { get; set; } = "";

    /// <summary>导出时该账户是否启用了签名。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>签名 HTML（与设置里的富文本签名同一套格式）。</summary>
    public string Html { get; set; } = "";
}

/// <summary>签名分享文件的外层结构（带格式标识与版本号，方便以后扩展）。</summary>
public sealed class SignatureShareDocument
{
    public const string FormatId = "zungmail.signatures";

    /// <summary>改名前（MailClient）导出的文件格式标识；导入时一并接受，导出仍写新标识。</summary>
    public const string LegacyFormatId = "mailclient.signatures";

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = SignatureShareService.CurrentVersion;
    public string App { get; set; } = "ZungMail";
    public string ExportedUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public List<SignatureShareEntry> Signatures { get; set; } = new();
}

/// <summary>解析结果：失败时给出可直接展示给用户的原因。</summary>
public sealed record SignatureShareParseResult(
    bool Ok,
    string Message,
    IReadOnlyList<SignatureShareEntry> Entries)
{
    public static SignatureShareParseResult Fail(string message) =>
        new(false, message, Array.Empty<SignatureShareEntry>());
}

/// <summary>一条导入项匹配到的本地账户。</summary>
public sealed record SignatureImportMatch(long AccountId, SignatureShareEntry Entry);

/// <summary>导入计划：命中本地账户的条目 + 本地没有的邮箱地址。</summary>
public sealed record SignatureImportPlan(
    IReadOnlyList<SignatureImportMatch> Matched,
    IReadOnlyList<string> UnknownAccounts)
{
    public bool HasAnything => Matched.Count > 0;
}

/// <summary>
/// 签名的导出 / 导入：导出为带版本号的 JSON，导入时校验格式并按邮箱地址匹配账户。
/// 纯逻辑、不碰文件系统与 UI，便于单测。
/// </summary>
public static class SignatureShareService
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

    /// <summary>把各账户的签名导出成分享文件内容。</summary>
    public static string Serialize(IEnumerable<SignatureShareEntry> entries, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var document = new SignatureShareDocument
        {
            ExportedUtc = (now ?? DateTimeOffset.UtcNow).ToString("O"),
            Signatures = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Account))
                .OrderBy(entry => entry.Account, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new SignatureShareEntry
                {
                    Account = entry.Account.Trim(),
                    Enabled = entry.Enabled,
                    Html = entry.Html ?? ""
                })
                .ToList()
        };

        return JsonSerializer.Serialize(document, WriteOptions);
    }

    /// <summary>解析导入文件；格式不符时返回可直接展示的原因。</summary>
    public static SignatureShareParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return SignatureShareParseResult.Fail("文件内容为空。");

        SignatureShareDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SignatureShareDocument>(json, ReadOptions);
        }
        catch (JsonException)
        {
            return SignatureShareParseResult.Fail("文件不是有效的 JSON，可能已损坏或被改动过。");
        }

        if (document is null)
            return SignatureShareParseResult.Fail("文件不是有效的签名文件。");

        if (!IsKnownFormat(document.Format))
            return SignatureShareParseResult.Fail("这不是 ZungMail 导出的签名文件。");

        if (document.Version > CurrentVersion)
            return SignatureShareParseResult.Fail(
                $"文件版本 {document.Version} 来自更新的客户端，请先升级本机版本再导入。");

        var entries = document.Signatures
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Account))
            .ToList();

        if (entries.Count == 0)
            return SignatureShareParseResult.Fail("文件里没有可导入的签名。");

        return new SignatureShareParseResult(true, $"共 {entries.Count} 个账户的签名。", entries);
    }

    /// <summary>
    /// 认新标识，也认改名前的 <see cref="SignatureShareDocument.LegacyFormatId"/>：
    /// 同事手里可能还留着旧版导出的签名文件。
    /// </summary>
    private static bool IsKnownFormat(string? format)
        => string.Equals(format, SignatureShareDocument.FormatId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(format, SignatureShareDocument.LegacyFormatId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按邮箱把导入项匹配到本地账户（大小写不敏感）；本地没有的邮箱原样返回，供界面提示。
    /// 同一邮箱在文件里出现多次时以最后一条为准。
    /// </summary>
    public static SignatureImportPlan Plan(
        IEnumerable<SignatureShareEntry> entries,
        IEnumerable<(long AccountId, string Email)> accounts)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(accounts);

        var byEmail = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (accountId, email) in accounts)
        {
            if (string.IsNullOrWhiteSpace(email)) continue;
            byEmail[email.Trim()] = accountId;
        }

        var matched = new List<SignatureImportMatch>();
        var unknown = new List<string>();

        foreach (var entry in entries)
        {
            var email = entry.Account?.Trim() ?? "";
            if (email.Length == 0) continue;

            if (!byEmail.TryGetValue(email, out var accountId))
            {
                if (!unknown.Contains(email, StringComparer.OrdinalIgnoreCase)) unknown.Add(email);
                continue;
            }

            // 同一账户出现多条：后面的覆盖前面的
            matched.RemoveAll(m => m.AccountId == accountId);
            matched.Add(new SignatureImportMatch(accountId, entry));
        }

        return new SignatureImportPlan(matched, unknown);
    }
}
