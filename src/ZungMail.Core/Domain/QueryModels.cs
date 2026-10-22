namespace ZungMail.Core.Domain;

/// <summary>
/// 列表查询条件（FR-04 统一收件箱 / FR-06 全文搜索）。
/// 采用分页约束（NFR-P4）：禁止无 <see cref="Take"/> 的全量读取。
/// </summary>
public sealed class MessageQuery
{
    /// <summary>null 表示跨账户（统一收件箱）。</summary>
    public long? AccountId { get; set; }

    /// <summary>指定单个文件夹。</summary>
    public long? FolderId { get; set; }

    /// <summary>按语义类型过滤（统一收件箱 = Inbox）。</summary>
    public FolderKind? FolderKind { get; set; }

    /// <summary>按本地标签过滤（标签视图：跨文件夹聚合）。</summary>
    public long? TagId { get; set; }

    /// <summary>只取指定邮件（用于从跟进 / 案件窗口精确定位一封邮件）。</summary>
    public long? EmailId { get; set; }

    /// <summary>按行业邮件类型过滤（订舱确认 / SI / VGM / 甩柜 …）。</summary>
    public MailType? MailType { get; set; }

    /// <summary>全文搜索关键词，主题/发件人/收件人/正文（FR-06）。</summary>
    public string? SearchText { get; set; }

    // ---------------- 结构化筛选（对齐 Foxmail「全部搜索条件」面板） ----------------

    /// <summary>发件人 / 域名包含。</summary>
    public string? FromText { get; set; }

    /// <summary>收件人（含抄送 / 密送）包含。</summary>
    public string? ToText { get; set; }

    /// <summary>主题包含。</summary>
    public string? SubjectText { get; set; }

    /// <summary>全文（正文纯文本）包含。</summary>
    public string? BodyText { get; set; }

    /// <summary>附件名包含。</summary>
    public string? FileNameText { get; set; }

    /// <summary>true=只看有附件，false=只看无附件，null=不限。</summary>
    public bool? HasAttachment { get; set; }

    /// <summary>true=只看有标签，false=只看无标签，null=不限（要指定某个标签时用 <see cref="TagId"/>）。</summary>
    public bool? HasAnyTag { get; set; }

    /// <summary>true=只看有提醒（跟进 / 待办），false=只看无提醒，null=不限。</summary>
    public bool? HasReminder { get; set; }

    /// <summary>本地备注包含。</summary>
    public string? NoteText { get; set; }

    /// <summary>起始时间（含）。</summary>
    public DateTimeOffset? SinceUtc { get; set; }

    /// <summary>结束时间（不含）。</summary>
    public DateTimeOffset? BeforeUtc { get; set; }

    public MailListFilter Filter { get; set; } = MailListFilter.All;

    public int Skip { get; set; }

    private int _take = 100;

    /// <summary>分页大小，上限 500，避免误用导致全量读取。</summary>
    public int Take
    {
        get => _take;
        set => _take = Math.Clamp(value, 1, 500);
    }

    /// <summary>是否按会话归并（FR-05）。</summary>
    public bool GroupByThread { get; set; }

    /// <summary>排除垃圾箱与已删除邮件。</summary>
    public bool ExcludeTrash { get; set; } = true;
}

/// <summary>分页结果。</summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }

    public static PagedResult<T> Empty { get; } = new();
}

/// <summary>会话分组（FR-05）。</summary>
public sealed class ThreadGroup
{
    public string ThreadId { get; init; } = "";

    /// <summary>代表邮件：会话中最新的一封，用于列表行展示。</summary>
    public EmailSummary Representative { get; init; } = new();

    public int Count { get; init; }
    public int UnreadCount { get; init; }
    public bool HasAttachment { get; init; }
    public IReadOnlyList<EmailSummary> Items { get; init; } = Array.Empty<EmailSummary>();

    public bool IsThread => Count > 1;
    public string CountDisplay => Count > 1 ? Count.ToString() : "";
}
