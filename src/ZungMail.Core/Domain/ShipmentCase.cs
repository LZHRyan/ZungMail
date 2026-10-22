namespace ZungMail.Core.Domain;

/// <summary>一票货案件状态。一期只有「进行中 / 已关闭」，状态机细化留给后续。</summary>
public enum ShipmentCaseStatus
{
    Open = 0,
    Closed = 1
}

/// <summary>
/// 一票货的业务阶段，由该票货收到的邮件类型推进（只前进不回退）。
/// 异常邮件（查验 / 甩柜 / 延误）不改阶段，只打上异常标记。
/// </summary>
public enum CaseStage
{
    New = 0,
    Quoted = 1,
    Booked = 2,
    SiReceived = 3,
    VgmReceived = 4,
    BlDraft = 5,
    Arrived = 6,
    Billing = 7,
    Customs = 8,
    Departed = 9
}

/// <summary>案件阶段的展示名与「邮件类型 → 阶段」映射。</summary>
public static class CaseStages
{
    public static string LabelOf(CaseStage stage) => stage switch
    {
        CaseStage.Quoted => "已报价",
        CaseStage.Booked => "已订舱",
        CaseStage.SiReceived => "已补料",
        CaseStage.VgmReceived => "已 VGM",
        CaseStage.BlDraft => "提单草稿",
        CaseStage.Arrived => "已到港",
        CaseStage.Billing => "对账中",
        CaseStage.Customs => "已报关",
        CaseStage.Departed => "已开船",
        _ => "新建"
    };

    /// <summary>邮件类型推进到的阶段；异常与普通邮件返回 <see cref="CaseStage.New"/>（不推进）。</summary>
    public static CaseStage FromMailType(MailType type) => type switch
    {
        MailType.Quotation => CaseStage.Quoted,
        MailType.BookingConfirmation => CaseStage.Booked,
        MailType.ShippingInstruction => CaseStage.SiReceived,
        MailType.Vgm => CaseStage.VgmReceived,
        MailType.DraftBillOfLading => CaseStage.BlDraft,
        MailType.ArrivalNotice => CaseStage.Arrived,
        MailType.DebitNote => CaseStage.Billing,
        MailType.CustomsInspection => CaseStage.Customs,
        _ => CaseStage.New
    };
}

/// <summary>
/// 一次索引写入：一封邮件的业务标识与时效节点。
/// 案件归并（同一票货的邮件挂到同一个案件）在仓储层按这批标识完成。
/// </summary>
public sealed class ShipmentIndexEntry
{
    public long EmailId { get; init; }

    public DateTimeOffset MessageDate { get; init; }

    public IReadOnlyList<FreightReference> References { get; init; } = Array.Empty<FreightReference>();

    public IReadOnlyList<FreightDeadline> Deadlines { get; init; } = Array.Empty<FreightDeadline>();

    /// <summary>该邮件的行业类型：用于推进案件阶段与标记异常。</summary>
    public MailType MailType { get; init; } = MailType.Unknown;
}

/// <summary>时效看板条目：一条节点 + 它所属的邮件与案件（可能没有案件）。</summary>
public sealed record DeadlineBoardItem(
    long EmailId,
    long CaseId,
    string CaseRef,
    FreightDeadlineKind Kind,
    DateTimeOffset DueUtc,
    string RawText,
    string Subject,
    string FromDisplay,
    DateTimeOffset MessageDate)
{
    public string KindLabel => FreightDeadline.LabelOf(Kind);

    public string KindShort => FreightDeadline.ShortOf(Kind);

    public string KindColor => FreightDeadline.ColorOf(Kind);

    public string DueDisplay => DisplayFormat.FullDate(DueUtc);

    public string DueDayDisplay => DueUtc.ToLocalTime().ToString("MM-dd HH:mm");

    public bool HasCase => CaseId > 0;

    public DeadlineUrgency Urgency => UrgencyOf(DateTimeOffset.Now);

    public string UrgencyColor => FreightDeadline.UrgencyColor(Urgency);

    /// <summary>「距截关 4 小时」/「已超时 2 小时」。</summary>
    public string CountdownText
    {
        get
        {
            var now = DateTimeOffset.Now;
            return new FreightDeadline { Kind = Kind, DueUtc = DueUtc }.Countdown(now);
        }
    }

    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? "（无主题）" : Subject;

    /// <summary>看板第一行：有案件号显示案件号，否则用邮件主题兜底。</summary>
    public string HeaderText => string.IsNullOrWhiteSpace(CaseRef) ? SubjectDisplay : CaseRef;

    public string Tooltip => string.IsNullOrWhiteSpace(RawText)
        ? $"{KindLabel} {DueDisplay}"
        : $"{KindLabel} {DueDisplay}\n原文：{RawText}";

    public DeadlineUrgency UrgencyOf(DateTimeOffset now)
        => new FreightDeadline { Kind = Kind, DueUtc = DueUtc }.Urgency(now);
}

/// <summary>案件列表项：一票货的单号、邮件数、最近往来与最近一个时效节点。</summary>
public sealed record ShipmentCaseSummary(
    long Id,
    FreightReferenceKind PrimaryKind,
    string PrimaryRef,
    ShipmentCaseStatus Status,
    int MessageCount,
    DateTimeOffset LastActivityUtc,
    DateTimeOffset? NextDeadlineUtc,
    CaseStage Stage = CaseStage.New,
    bool HasException = false)
{
    public string PrimaryKindLabel => FreightReference.KindLabelOf(PrimaryKind);

    public string PrimaryKindShort => FreightReference.KindShortOf(PrimaryKind);

    public string PrimaryKindColor => FreightReference.KindColorOf(PrimaryKind);

    public string StatusText => Status == ShipmentCaseStatus.Closed ? "已关闭" : "进行中";

    /// <summary>业务阶段（由邮件类型推进）。</summary>
    public string StageText => CaseStages.LabelOf(Stage);

    public bool HasStage => Stage != CaseStage.New;

    public string ExceptionText => HasException ? "有异常" : "";

    public bool IsClosed => Status == ShipmentCaseStatus.Closed;

    public string MessageCountDisplay => $"{MessageCount} 封";

    public string LastActivityDisplay => DisplayFormat.RelativeDate(LastActivityUtc);

    public string NextDeadlineDisplay => NextDeadlineUtc is { } due
        ? FormatNextDeadline(due)
        : "无待办节点";

    private static string FormatNextDeadline(DateTimeOffset due)
    {
        var remaining = due - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "节点已过";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays} 天后有节点";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours} 小时后有节点";
        return "1 小时内到期";
    }
}

/// <summary>案件时间轴上的一封邮件。</summary>
public sealed record CaseMessageRow(
    long EmailId,
    string Subject,
    string FromDisplay,
    string FromEmail,
    DateTimeOffset DateUtc,
    bool HasAttachment,
    bool IsUnread,
    MailType MailType = MailType.Unknown)
{
    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? "（无主题）" : Subject;

    public string DateDisplay => DisplayFormat.FullDate(DateUtc);

    public string FromText => string.IsNullOrWhiteSpace(FromDisplay) ? FromEmail : FromDisplay;

    public bool HasType => MailType != MailType.Unknown;

    public string TypeLabel => MailTypes.LabelOf(MailType);

    public string TypeColor => MailTypes.ColorOf(MailType);

    public bool IsException => MailTypes.IsException(MailType);
}

/// <summary>案件资料包里的一个附件（已下载才有 LocalPath）。</summary>
public sealed record CaseAttachmentRow(
    long EmailId,
    string FileName,
    string? LocalPath,
    long Size,
    string Subject)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath);

    public string SizeDisplay => DisplayFormat.FileSize(Size);
}

/// <summary>案件详情：时间轴 + 时效节点 + 附件。</summary>
public sealed record ShipmentCaseDetail(
    ShipmentCaseSummary Summary,
    IReadOnlyList<CaseMessageRow> Messages,
    IReadOnlyList<DeadlineBoardItem> Deadlines,
    IReadOnlyList<CaseAttachmentRow> Attachments);

/// <summary>资料包导出用的邮件快照。</summary>
public sealed record CaseExportMessage(
    long EmailId,
    string Subject,
    string FromDisplay,
    string ToDisplay,
    string CcDisplay,
    DateTimeOffset DateUtc,
    string? EmlPath,
    string? BodyHtml,
    string? BodyText,
    IReadOnlyList<CaseExportAttachment> Attachments);

/// <summary>资料包导出用的附件快照。</summary>
public sealed record CaseExportAttachment(string FileName, long Size, string? LocalPath);

/// <summary>一票货资料包内容：某票货的全部邮件与附件。</summary>
public sealed record ShipmentCaseExport(
    long CaseId,
    string PrimaryRef,
    IReadOnlyList<CaseExportMessage> Messages);
