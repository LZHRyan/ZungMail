namespace ZungMail.Core.Domain;

/// <summary>清理触发方式。</summary>
public enum CleanupMode
{
    /// <summary>按筛选条件一键清理。</summary>
    Filter = 0,

    /// <summary>手动勾选清理。</summary>
    Selected = 1,

    /// <summary>自动定期清理。</summary>
    Auto = 2
}

/// <summary>清理任务状态。</summary>
public enum CleanupStatus
{
    Running = 0,
    Completed = 1,
    Cancelled = 2,
    Undone = 3,
    Purged = 4
}

/// <summary>
/// 历史邮件清理筛选条件。默认只覆盖垃圾箱与广告邮件，星标/未读/草稿默认保护，
/// 避免误删重要邮件。
/// </summary>
public sealed class CleanupFilter
{
    public long? AccountId { get; set; }
    public long? FolderId { get; set; }

    /// <summary>排除这些账户（用于跳过“已停止使用”的账户，避免清理其本地邮件）。</summary>
    public List<long> ExcludeAccountIds { get; set; } = new();

    public bool IncludeInbox { get; set; }
    public bool IncludeSent { get; set; }
    public bool IncludeTrash { get; set; } = true;
    public bool IncludeJunk { get; set; } = true;
    public bool IncludeArchive { get; set; }

    /// <summary>只清理该时间之前收到的邮件。</summary>
    public DateTimeOffset? BeforeUtc { get; set; }

    /// <summary>只清理该时间之后收到的邮件。</summary>
    public DateTimeOffset? AfterUtc { get; set; }

    /// <summary>邮件大小下限（字节）。</summary>
    public long MinSizeBytes { get; set; }

    /// <summary>邮件大小上限（字节）。</summary>
    public long MaxSizeBytes { get; set; }

    /// <summary>只清理带附件的邮件。</summary>
    public bool OnlyWithAttachments { get; set; }

    /// <summary>附件类型筛选（如 pdf、zip、docx，不带点号）。为空表示不限。</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>保护星标邮件（默认开启）。</summary>
    public bool ProtectStarred { get; set; } = true;

    /// <summary>保护未读邮件（默认开启）。</summary>
    public bool ProtectUnread { get; set; } = true;

    /// <summary>
    /// 仅清正文与附件：命中邮件本身保留（邮件头、文件夹、标志都不动），
    /// 只清空已下载的正文、删掉附件记录与 <c>eml</c> 下的附件文件。
    /// 这种模式不写隔离区快照，因此不可撤销；阅读时按需重新下载。
    /// </summary>
    public bool ClearBodyOnly { get; set; }

    public string Describe()
    {
        var parts = new List<string>();
        if (ClearBodyOnly) parts.Add("仅清正文与附件（保留邮件头）");
        if (BeforeUtc is { } before) parts.Add($"{before.LocalDateTime:yyyy-MM-dd} 之前");
        if (AfterUtc is { } after) parts.Add($"{after.LocalDateTime:yyyy-MM-dd} 之后");
        if (MinSizeBytes > 0) parts.Add($"大于 {DisplayFormat.FileSize(MinSizeBytes)}");
        if (MaxSizeBytes > 0) parts.Add($"小于 {DisplayFormat.FileSize(MaxSizeBytes)}");
        if (OnlyWithAttachments) parts.Add("仅带附件");
        if (Extensions.Count > 0) parts.Add("附件类型 " + string.Join('/', Extensions));
        if (ProtectStarred) parts.Add("保护星标");
        if (ProtectUnread) parts.Add("保护未读");
        return parts.Count == 0 ? "全部邮件" : string.Join(" · ", parts);
    }
}

/// <summary>清理预览条目。</summary>
public sealed class CleanupPreviewItem
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public long FolderId { get; set; }
    public string FolderName { get; set; } = "";
    public string Subject { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public DateTimeOffset DateUtc { get; set; }
    public long Size { get; set; }
    public long AttachmentBytes { get; set; }
    public MessageFlags Flags { get; set; }

    public bool IsStarred => (Flags & MessageFlags.Flagged) != 0;
    public bool IsUnread => (Flags & MessageFlags.Seen) == 0;
    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? "（无主题）" : Subject;
    public string DateDisplay => DisplayFormat.FullDate(DateUtc);
    public string SizeDisplay => DisplayFormat.FileSize(Size + AttachmentBytes);
}

/// <summary>清理预览结果（列表 + 汇总）。</summary>
public sealed class CleanupPreview
{
    public IReadOnlyList<CleanupPreviewItem> Items { get; init; } = Array.Empty<CleanupPreviewItem>();
    public int TotalCount { get; init; }
    public long TotalBytes { get; init; }
    public long AttachmentBytes { get; init; }

    /// <summary>
    /// 按当前模式预计能释放的字节数：整封删除＝邮件本身 + 附件；
    /// 「仅清正文与附件」＝已下载正文长度 + 附件大小（预告口径与实际清理一致）。
    /// </summary>
    public long ReclaimableBytes { get; init; }

    public string TotalCountDisplay => $"{TotalCount:N0} 封";
    public string TotalSizeDisplay => DisplayFormat.FileSize(TotalBytes + AttachmentBytes);
    public string ReclaimableDisplay => DisplayFormat.FileSize(ReclaimableBytes);
}

/// <summary>清理任务（可追溯的审计记录）。</summary>
public sealed class CleanupJob
{
    public long Id { get; set; }
    public CleanupMode Mode { get; set; }
    public CleanupStatus Status { get; set; }
    public string FilterJson { get; set; } = "";
    public int TotalCount { get; set; }
    public int ProcessedCount { get; set; }
    public long TotalBytes { get; set; }
    public long FreedBytes { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public string Note { get; set; } = "";

    /// <summary>
    /// 是否正确入档为「仅清正文与附件」任务。该模式不写隔离区快照，
    /// 因此没有可撤销 / 可彻底删除的数据；从 <see cref="FilterJson"/> 反序列化得到。
    /// </summary>
    public bool IsBodyOnly { get; set; }

    public bool CanUndo => !IsBodyOnly && Status is CleanupStatus.Completed or CleanupStatus.Cancelled;
    public bool CanPurge => !IsBodyOnly && Status is CleanupStatus.Completed or CleanupStatus.Cancelled;
    public bool CanResume => Status == CleanupStatus.Cancelled;

    public string ModeDisplay => Mode switch
    {
        CleanupMode.Selected => IsBodyOnly ? "手动勾选 · 仅清正文" : "手动勾选",
        CleanupMode.Auto => "自动清理",
        _ => IsBodyOnly ? "按条件 · 仅清正文" : "按条件一键清理"
    };

    public string StatusDisplay => Status switch
    {
        CleanupStatus.Running => "进行中",
        CleanupStatus.Completed => IsBodyOnly ? "已完成（仅清正文，不可撤销）" : "已完成（可撤销）",
        CleanupStatus.Cancelled => IsBodyOnly ? "已中断（可恢复）" : "已中断（可撤销/恢复）",
        CleanupStatus.Undone => "已撤销",
        CleanupStatus.Purged => "已彻底删除",
        _ => "未知"
    };

    public string SummaryDisplay => IsBodyOnly
        ? $"{ProcessedCount:N0} / {TotalCount:N0} 封 · 已清空正文与附件 {DisplayFormat.FileSize(TotalBytes)}"
        : $"{ProcessedCount:N0} / {TotalCount:N0} 封 · 待释放 {DisplayFormat.FileSize(TotalBytes)}"
          + (FreedBytes > 0 ? $" · 已释放 {DisplayFormat.FileSize(FreedBytes)}" : "");

    public string StartedDisplay => DisplayFormat.FullDate(StartedUtc);

    public string TimeDisplay => FinishedUtc is { } finished
        ? $"{DisplayFormat.FullDate(StartedUtc)} → {DisplayFormat.FullDate(finished)}"
        : DisplayFormat.FullDate(StartedUtc);
}

/// <summary>清理进度。</summary>
public sealed class CleanupProgress
{
    public int Processed { get; init; }
    public int Total { get; init; }
    public long Bytes { get; init; }
    public string CurrentSubject { get; init; } = "";

    public double Percent => Total <= 0 ? 0 : Math.Round(Processed * 100.0 / Total, 1);

    public string Text => Total <= 0
        ? "正在准备清理…"
        : $"正在清理 {Processed}/{Total} 封（{Percent:0.#}%）";
}
