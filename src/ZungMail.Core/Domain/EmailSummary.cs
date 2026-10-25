namespace ZungMail.Core.Domain;

/// <summary>
/// 邮件头摘要：本地只同步邮件头时，列表即以此渲染。
/// </summary>
public class EmailSummary
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public long FolderId { get; set; }

    /// <summary>IMAP UID，与 FolderId 联合唯一。</summary>
    public uint Uid { get; set; }

    /// <summary>RFC 5322 Message-ID。</summary>
    public string MessageId { get; set; } = "";

    /// <summary>会话归并键（FR-05），由 References/Message-ID 推导。</summary>
    public string ThreadId { get; set; } = "";

    public string Subject { get; set; } = "";
    public string FromName { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public DateTimeOffset DateUtc { get; set; }
    public string Preview { get; set; } = "";
    public MessageFlags Flags { get; set; }
    public bool HasAttachment { get; set; }
    public long Size { get; set; }

    /// <summary>所属文件夹显示名（仅本地查询时填充，不参与同步）。</summary>
    public string FolderName { get; set; } = "";

    /// <summary>发件人请求的阅读收条地址（Disposition-Notification-To）；空表示未请求。</summary>
    public string MdnTo { get; set; } = "";

    /// <summary>行业邮件类型（识别结果；Unknown 表示未识别）。</summary>
    public MailType IndustryType { get; set; } = MailType.Unknown;

    /// <summary>
    /// 陌生发件人：入库时该地址既不在通讯录、也没有更早的来信（判定见 <c>StrangerSenderPolicy</c>）。
    /// 结论在收信那一刻固化，之后不再变化；存量邮件与全量重同步一律为 false。
    /// </summary>
    public bool IsStranger { get; set; }

    /// <summary>界面提示用：陌生发件人的说明文案。</summary>
    public string StrangerHint => "首次收到该地址来信，注意核对附件、账号与付款信息";

    /// <summary>
    /// 退信（投递失败退回）：入库时按 MIME 结构 / 发件人 / 主题判定（见 <c>BounceMailPolicy</c>），
    /// 正文下载后再解析 delivery-status 补全细节。存量邮件与全量重同步一律为 false。
    /// </summary>
    public bool IsBounce { get; set; }

    /// <summary>退信里的失败收件人（Final-Recipient），未解析出来时为空。</summary>
    public string BounceRecipient { get; set; } = "";

    /// <summary>退信里的 SMTP 状态码（Status），如 5.1.1。</summary>
    public string BounceStatus { get; set; } = "";

    /// <summary>退信里的诊断信息（Diagnostic-Code），如 550 5.1.1 User unknown。</summary>
    public string BounceDetail { get; set; } = "";

    /// <summary>是否解析出了具体的失败收件人 / 状态码 / 诊断。</summary>
    public bool HasBounceInfo =>
        !string.IsNullOrWhiteSpace(BounceRecipient) ||
        !string.IsNullOrWhiteSpace(BounceStatus) ||
        !string.IsNullOrWhiteSpace(BounceDetail);

    /// <summary>列表角标的悬停提示。</summary>
    public string BounceHint => HasBounceInfo
        ? $"退信：{BounceDetailLine}"
        : "退信：这封邮件没有送达收件人";

    /// <summary>阅读区提示条第二行：有细节列细节，没有就引导去看正文。</summary>
    public string BounceDetailLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(BounceRecipient)) parts.Add($"收件人 {BounceRecipient}");

            var code = string.Join(' ', new[] { BounceStatus, BounceDetail }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
            if (code.Length > 0) parts.Add(code);

            return parts.Count > 0 ? string.Join(" · ", parts) : "展开正文查看失败原因";
        }
    }

    // ---- 派生属性（供界面直接绑定）----
    public bool IsUnread => (Flags & MessageFlags.Seen) == 0;
    public bool IsStarred => (Flags & MessageFlags.Flagged) != 0;
    public bool IsReplied => (Flags & MessageFlags.Answered) != 0;
    public bool IsDraftFlag => (Flags & MessageFlags.Draft) != 0;
    public bool HasIndustryType => IndustryType != MailType.Unknown;
    public string IndustryTypeLabel => MailTypes.LabelOf(IndustryType);
    public string IndustryTypeShort => MailTypes.ShortOf(IndustryType);
    public string IndustryTypeColor => MailTypes.ColorOf(IndustryType);
    public bool IsExceptionMail => MailTypes.IsException(IndustryType);
    public string FromDisplay => string.IsNullOrWhiteSpace(FromName) ? FromEmail : FromName;
    public string Initial => DisplayFormat.Initial(FromDisplay);
    public string AvatarColor => DisplayFormat.ColorFor(FromEmail);
    public string DateDisplay => DisplayFormat.RelativeDate(DateUtc);
    public string SizeDisplay => DisplayFormat.FileSize(Size);
    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? "（无主题）" : Subject;
}

/// <summary>完整邮件（含正文与附件），读取邮件时按需下载。</summary>
public sealed class EmailDetail : EmailSummary
{
    public List<EmailAddress> To { get; set; } = new();
    public List<EmailAddress> Cc { get; set; } = new();
    public List<EmailAddress> Bcc { get; set; } = new();
    public EmailAddress? ReplyTo { get; set; }

    /// <summary>References 链，用于线程归并。</summary>
    public List<string> References { get; set; } = new();

    public string BodyHtml { get; set; } = "";
    public string BodyText { get; set; } = "";
    public BodyState BodyState { get; set; } = BodyState.NotDownloaded;

    public List<AttachmentInfo> Attachments { get; set; } = new();

    public bool HasBody => BodyState == BodyState.Downloaded;
    public string ToDisplay => string.Join("; ", To.Select(a => a.Display));
    public string CcDisplay => string.Join("; ", Cc.Select(a => a.Display));
    public string BccDisplay => string.Join("; ", Bcc.Select(a => a.Display));
    public string ReplyToDisplay => ReplyTo?.Address ?? FromEmail;
    public string FullDateDisplay => DisplayFormat.FullDate(DateUtc);
}
