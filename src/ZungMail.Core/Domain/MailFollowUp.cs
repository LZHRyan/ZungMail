namespace ZungMail.Core.Domain;

/// <summary>等待回复的跟进状态。</summary>
public enum FollowUpState
{
    /// <summary>等待回复中（到点检查）。</summary>
    Waiting = 0,

    /// <summary>回复条件已满足，自动结束。</summary>
    Satisfied = 1,

    /// <summary>用户主动取消。</summary>
    Cancelled = 2
}

/// <summary>
/// 「等待回复」跟进：把「等客户补料 / 等船司放舱 / 等代理确认费用」变成可跟踪的状态。
/// <para>
/// 判定复用会话内入站邮件（同一 thread 中、源邮件之后、发件人属于要求的收件人集合）。
/// 一期只做「全部回复」与「至少 N 人回复」两种条件；满足后自动结束，不满足则按检查时间提醒一次。
/// 不自动重发、不自动催信——催信永远由人确认（与 Foxmail 的克制设计一致）。
/// </para>
/// </summary>
public sealed class MailFollowUp
{
    public long Id { get; set; }

    /// <summary>发起跟进的邮件（提醒时定位到它）。</summary>
    public long EmailId { get; set; }

    /// <summary>会话键；回复判定在同一个 thread 内进行。</summary>
    public string ThreadId { get; set; } = "";

    public string Subject { get; set; } = "";

    /// <summary>原邮件发件人（仅展示）。</summary>
    public string FromEmail { get; set; } = "";

    /// <summary>原邮件时间；只统计此时间之后的回复。</summary>
    public DateTimeOffset SentUtc { get; set; }

    /// <summary>需要回复的地址（小写，已去重）。</summary>
    public IReadOnlyList<string> Recipients { get; set; } = Array.Empty<string>();

    /// <summary>true = 要求全部回复；false = 至少 <see cref="MinReplies"/> 人回复。</summary>
    public bool RequireAll { get; set; } = true;

    public int MinReplies { get; set; } = 1;

    /// <summary>是否把抄送人也算进需要回复的集合。</summary>
    public bool IncludeCc { get; set; }

    /// <summary>下次检查时间（UTC）。</summary>
    public DateTimeOffset NextCheckUtc { get; set; }

    public FollowUpState State { get; set; } = FollowUpState.Waiting;

    /// <summary>最近一次提醒时间；为空表示这一轮还没提醒过。</summary>
    public DateTimeOffset? NotifiedUtc { get; set; }

    public string Note { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public bool IsActive => State == FollowUpState.Waiting;

    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? "（无主题）" : Subject;

    public string StateText => State switch
    {
        FollowUpState.Satisfied => "已回复",
        FollowUpState.Cancelled => "已取消",
        _ => "等待回复"
    };

    public string RecipientsDisplay => Recipients.Count == 0 ? "（无收件人）" : string.Join("、", Recipients);

    public string NextCheckDisplay => DisplayFormat.FullDate(NextCheckUtc);

    public string RequirementText => RequireAll
        ? $"要求全部 {Recipients.Count} 人回复"
        : $"至少 {Math.Min(Math.Max(MinReplies, 1), Math.Max(Recipients.Count, 1))} 人回复";

    /// <summary>规范化收件人：去空白、去重、转小写。</summary>
    public static IReadOnlyList<string> NormalizeRecipients(IEnumerable<string?> addresses)
        => addresses
            .Select(address => address?.Trim().Trim('<', '>').ToLowerInvariant())
            .Where(address => !string.IsNullOrWhiteSpace(address) && address!.Contains('@'))
            .Select(address => address!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>会话内的一封入站邮件（用于判定谁回复了）。</summary>
public sealed record FollowUpReply(
    long EmailId,
    string FromEmail,
    string FromName,
    string Subject,
    DateTimeOffset DateUtc)
{
    public string FromDisplay => string.IsNullOrWhiteSpace(FromName) ? FromEmail : FromName;
}

/// <summary>一次跟进评估结果：谁回了、还差谁、是否满足条件。</summary>
public sealed class FollowUpStatus
{
    public required MailFollowUp FollowUp { get; init; }

    public IReadOnlyList<FollowUpReply> Replies { get; init; } = Array.Empty<FollowUpReply>();

    /// <summary>已回复的地址（保持原顺序）。</summary>
    public IReadOnlyList<string> Replied { get; init; } = Array.Empty<string>();

    /// <summary>仍未回复的地址。</summary>
    public IReadOnlyList<string> Pending { get; init; } = Array.Empty<string>();

    public bool IsSatisfied { get; init; }

    public string SubjectDisplay => FollowUp.SubjectDisplay;

    public string StateText => FollowUp.StateText;

    public string RequirementText => FollowUp.RequirementText;

    public string NextCheckDisplay => "下次检查 " + FollowUp.NextCheckDisplay;

    public bool IsActive => FollowUp.IsActive;

    public bool HasNote => !string.IsNullOrWhiteSpace(FollowUp.Note);

    /// <summary>提醒状态：已提醒的条目停在本轮，等用户「继续跟进」。</summary>
    public string NotifiedText => FollowUp.NotifiedUtc is { } notified
        ? $"已于 {DisplayFormat.FullDate(notified)} 提醒 · 待跟进"
        : "到点自动检查";

    public int RepliedCount => Replied.Count;

    public int TotalCount => FollowUp.Recipients.Count;

    public string ProgressText => $"{RepliedCount}/{TotalCount} 已回复";

    public string PendingText
    {
        get
        {
            if (Pending.Count == 0) return "全部已回复";
            var names = string.Join("、", Pending.Take(3));
            return Pending.Count <= 3 ? $"还差 {names}" : $"还差 {names} 等 {Pending.Count} 人";
        }
    }

    public string NoteDisplay => string.IsNullOrWhiteSpace(FollowUp.Note) ? "" : FollowUp.Note;
}
