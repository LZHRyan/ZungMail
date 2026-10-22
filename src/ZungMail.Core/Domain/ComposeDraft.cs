namespace ZungMail.Core.Domain;

/// <summary>
/// 写信窗口的本地草稿：只保存「还没发出去」的内容，用于自动保存与崩溃恢复。
/// 与服务器草稿箱（IMAP Drafts）无关——写服务器草稿需要 APPEND 且各服务端行为不一。
/// </summary>
public sealed class ComposeDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>写信模式（New / Reply / ReplyAll / Forward），恢复时用它决定窗口标题。</summary>
    public string Mode { get; set; } = "New";

    public long? AccountId { get; set; }

    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Bcc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";

    /// <summary>附件绝对路径（文件已不存在时恢复会跳过）。</summary>
    public List<string> Attachments { get; set; } = new();

    public string? InReplyTo { get; set; }
    public string? References { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>是否有值得保存的内容（全空时不必落盘，也不该弹「保存草稿」）。</summary>
    public bool HasContent()
        => !string.IsNullOrWhiteSpace(To)
           || !string.IsNullOrWhiteSpace(Cc)
           || !string.IsNullOrWhiteSpace(Bcc)
           || !string.IsNullOrWhiteSpace(Subject)
           || !string.IsNullOrWhiteSpace(Services.HtmlTextExtractor.ToPlainText(BodyHtml))
           || Attachments.Count > 0;
}
