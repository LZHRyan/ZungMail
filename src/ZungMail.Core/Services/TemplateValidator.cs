using System.Text;
using System.Text.RegularExpressions;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>模板校验问题（字段名与编辑区控件一一对应，便于定位提示）。</summary>
public sealed record TemplateValidationIssue(string Field, string Message);

/// <summary>模板校验结果：<see cref="Errors"/> 阻止保存，<see cref="Warnings"/> 仅提示。</summary>
public sealed record TemplateValidationResult(
    IReadOnlyList<TemplateValidationIssue> Errors,
    IReadOnlyList<TemplateValidationIssue> Warnings)
{
    public static TemplateValidationResult Empty { get; } =
        new(Array.Empty<TemplateValidationIssue>(), Array.Empty<TemplateValidationIssue>());

    public bool IsValid => Errors.Count == 0;

    public bool HasIssues => Errors.Count > 0 || Warnings.Count > 0;

    /// <summary>错误摘要（单行，用于状态栏 / 对话框）。</summary>
    public string ErrorSummary => string.Join("；", Errors.Select(e => e.Message));

    /// <summary>提示摘要（单行，用于状态栏）。</summary>
    public string WarningSummary => string.Join("；", Warnings.Select(w => w.Message));

    public string? ErrorFor(string field) =>
        Errors.FirstOrDefault(e => string.Equals(e.Field, field, StringComparison.Ordinal))?.Message;

    public string? WarningFor(string field) =>
        Warnings.FirstOrDefault(w => string.Equals(w.Field, field, StringComparison.Ordinal))?.Message;
}

/// <summary>
/// 模板合法性校验（FR-08）。纯逻辑、无 UI 依赖，供设置界面与单元测试共用。
/// 目标是「保存前就说清问题」，避免存下无法预览或无法套用的模板。
/// </summary>
public static partial class TemplateValidator
{
    public const int MaxNameLength = 40;
    public const int MaxSubjectLength = 200;
    public const int MaxDescriptionLength = 200;
    public const int MaxBodyLength = 200_000;

    /// <summary>正文 HTML 的字节上限（UTF-8），防止超大模板拖慢预览与写信。</summary>
    public const int MaxBodyBytes = 512 * 1024;

    /// <summary>校验模板；<paramref name="siblings"/> 用于重名检测（应包含除自身外的全部模板）。</summary>
    public static TemplateValidationResult Validate(MailTemplate template, IEnumerable<MailTemplate>? siblings = null)
    {
        var errors = new List<TemplateValidationIssue>();
        var warnings = new List<TemplateValidationIssue>();

        var name = (template.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors.Add(new TemplateValidationIssue(nameof(MailTemplate.Name), "请输入模板名称"));
        }
        else if (name.Length > MaxNameLength)
        {
            errors.Add(new TemplateValidationIssue(nameof(MailTemplate.Name),
                $"模板名称不能超过 {MaxNameLength} 个字符（当前 {name.Length} 个）"));
        }
        else if (siblings is not null &&
                 siblings.Any(t => !string.Equals(t.Id, template.Id, StringComparison.Ordinal) &&
                                   string.Equals((t.Name ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new TemplateValidationIssue(nameof(MailTemplate.Name), $"已存在同名模板「{name}」，请换一个名称"));
        }

        var subject = template.Subject ?? string.Empty;
        if (subject.Length > MaxSubjectLength)
        {
            errors.Add(new TemplateValidationIssue(nameof(MailTemplate.Subject),
                $"默认主题不能超过 {MaxSubjectLength} 个字符（当前 {subject.Length} 个）"));
        }
        else if (subject.Trim().Length == 0)
        {
            warnings.Add(new TemplateValidationIssue(nameof(MailTemplate.Subject), "默认主题为空，套用后需手动填写主题"));
        }

        var description = template.Description ?? string.Empty;
        if (description.Length > MaxDescriptionLength)
        {
            errors.Add(new TemplateValidationIssue(nameof(MailTemplate.Description),
                $"模板说明不能超过 {MaxDescriptionLength} 个字符（当前 {description.Length} 个）"));
        }

        var body = template.Body ?? string.Empty;
        if (body.Trim().Length == 0)
        {
            warnings.Add(new TemplateValidationIssue(nameof(MailTemplate.Body), "正文为空，套用后仅替换主题"));
        }
        else if (body.Length > MaxBodyLength)
        {
            errors.Add(new TemplateValidationIssue(nameof(MailTemplate.Body),
                $"正文过长（{body.Length:N0} 字符，上限 {MaxBodyLength:N0}）"));
        }
        else if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
        {
            errors.Add(new TemplateValidationIssue(nameof(MailTemplate.Body),
                $"正文体积过大（上限 {MaxBodyBytes / 1024} KB）"));
        }

        var danger = FindDangerousMarkup(body);
        if (danger is not null)
        {
            errors.Add(new TemplateValidationIssue(nameof(MailTemplate.Body), danger));
        }

        return new TemplateValidationResult(errors, warnings);
    }

    /// <summary>检测会破坏预览或带来安全风险的标记；命中时返回可直接展示的中文提示。</summary>
    public static string? FindDangerousMarkup(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        if (ScriptTagRegex().IsMatch(html))
            return "正文包含 <script> 脚本，预览与套用时会被拦截，请删除后再保存";
        if (FrameTagRegex().IsMatch(html))
            return "正文包含 <iframe> / <object> / <embed> 内嵌内容，预览与套用时会被拦截，请删除后再保存";
        if (JavaScriptUrlRegex().IsMatch(html))
            return "正文包含 javascript: 链接，预览与套用时会被拦截，请改为普通网址";
        if (InlineHandlerRegex().IsMatch(html))
            return "正文包含内联事件处理（如 onclick），预览与套用时会被拦截，请删除后再保存";

        return null;
    }

    [GeneratedRegex(@"<\s*script\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"<\s*(iframe|object|embed|base)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FrameTagRegex();

    [GeneratedRegex(@"javascript\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex JavaScriptUrlRegex();

    [GeneratedRegex(@"<[^>]+\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandlerRegex();
}
