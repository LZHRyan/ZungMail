namespace ZungMail.Core.Services;

/// <summary>发送前检查的输入（纯数据，便于单测）。</summary>
public sealed record ComposeCheckInput(
    string Subject,
    string BodyHtml,
    int AttachmentCount,
    int RecipientCount,
    bool IsReplying = false);

/// <summary>一条发送前提示（不阻断发送，由用户确认后继续）。</summary>
public sealed record ComposeWarning(string Code, string Message);

/// <summary>
/// 发送前检查：把「发出去之前还能补救」的问题挑出来，避免漏附件、空主题这类低级错误。
/// 只提示、不阻断——用户确认后仍然可以发送。
/// </summary>
public static class ComposeSendGuard
{
    /// <summary>正文里出现这些词时，认为用户打算附带附件。</summary>
    private static readonly string[] AttachmentKeywords =
    {
        "附件", "附上", "随附", "见附", "详见附", "attach", "enclos"
    };

    public static IReadOnlyList<ComposeWarning> Check(ComposeCheckInput input)
    {
        var warnings = new List<ComposeWarning>();

        if (string.IsNullOrWhiteSpace(input.Subject))
        {
            warnings.Add(new ComposeWarning(
                "empty-subject",
                "主题为空：收件人容易忽略，也可能被判为可疑邮件。"));
        }

        var body = HtmlTextExtractor.ToPlainText(input.BodyHtml ?? string.Empty);
        if (body.Length == 0)
        {
            warnings.Add(new ComposeWarning("empty-body", "正文为空。"));
        }
        else if (input.AttachmentCount == 0 &&
                 AttachmentKeywords.Any(k => body.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(new ComposeWarning(
                "missing-attachment",
                "正文提到「附件」，但这封邮件还没有添加任何附件。"));
        }

        if (input.RecipientCount > 20)
        {
            warnings.Add(new ComposeWarning(
                "many-recipients",
                $"收件人共 {input.RecipientCount} 个：确认是否需要群发，收件人之间会互相看到地址。"));
        }

        return warnings;
    }
}
