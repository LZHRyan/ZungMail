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
