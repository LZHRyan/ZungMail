using ZungMail.Core.Domain;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 「全部搜索条件」面板的取值（对齐 Foxmail 的搜索面板：发件人 / 收件人 / 主题 / 全文 /
/// 附件 / 标签 / 提醒 / 备注 / 时间）。
/// 条件直接映射到 <see cref="MessageQuery"/> 的结构化字段，不再拼回搜索字符串——
/// 这样「有标签 / 无标签 / 有提醒」这类布尔条件也有明确语义。
/// </summary>
public sealed record MailSearchFilter
{
    public string From { get; init; } = "";

    public string To { get; init; } = "";

    public string Subject { get; init; } = "";

    public string Body { get; init; } = "";

    public string FileName { get; init; } = "";

    /// <summary>true=有附件，false=无附件，null=不限。</summary>
    public bool? HasAttachment { get; init; }

    /// <summary>指定标签（优先于 <see cref="HasAnyTag"/>）。</summary>
    public long? TagId { get; init; }

    /// <summary>true=有标签，false=无标签，null=不限。</summary>
    public bool? HasAnyTag { get; init; }

    /// <summary>true=有提醒（跟进 / 待办），false=无提醒，null=不限。</summary>
    public bool? HasReminder { get; init; }

    public string Note { get; init; } = "";

    public DateTimeOffset? SinceUtc { get; init; }

    public DateTimeOffset? BeforeUtc { get; init; }

    /// <summary>是否至少有一个生效条件（决定要不要进「搜索结果」态）。</summary>
    public bool HasAnyCondition =>
        From.Length > 0
        || To.Length > 0
        || Subject.Length > 0
        || Body.Length > 0
        || FileName.Length > 0
        || Note.Length > 0
        || HasAttachment is not null
        || TagId is not null
        || HasAnyTag is not null
        || HasReminder is not null
        || SinceUtc is not null
        || BeforeUtc is not null;

    /// <summary>生效条件数量（给状态栏 / 标题展示用）。</summary>
    public int ConditionCount
    {
        get
        {
            var count = 0;
            if (From.Length > 0) count++;
            if (To.Length > 0) count++;
            if (Subject.Length > 0) count++;
            if (Body.Length > 0) count++;
            if (FileName.Length > 0) count++;
            if (Note.Length > 0) count++;
            if (HasAttachment is not null) count++;
            if (TagId is not null || HasAnyTag is not null) count++;
            if (HasReminder is not null) count++;
            if (SinceUtc is not null || BeforeUtc is not null) count++;
            return count;
        }
    }

    /// <summary>把条件写进查询对象（全部是「与」关系）。</summary>
    public void ApplyTo(MessageQuery query)
    {
        if (From.Length > 0) query.FromText = From;
        if (To.Length > 0) query.ToText = To;
        if (Subject.Length > 0) query.SubjectText = Subject;
        if (Body.Length > 0) query.BodyText = Body;
        if (FileName.Length > 0) query.FileNameText = FileName;
        if (Note.Length > 0) query.NoteText = Note;
        if (HasAttachment is { } hasAttachment) query.HasAttachment = hasAttachment;
        if (TagId is { } tagId) query.TagId = tagId;
        else if (HasAnyTag is { } hasAnyTag) query.HasAnyTag = hasAnyTag;
        if (HasReminder is { } hasReminder) query.HasReminder = hasReminder;
        if (SinceUtc is { } since) query.SinceUtc = since;
        if (BeforeUtc is { } before) query.BeforeUtc = before;
    }
}
