using ZungMail.Core.Services;

namespace ZungMail.Core.Domain;

/// <summary>
/// 邮件标签：本地业务状态，不回写 IMAP。标签按账户隔离，
/// 每个标签可绑定一个单键（数字 / 字母 / F1-F12），在邮件列表选中后按一下即可打 / 取消。
/// </summary>
public sealed class MailTag
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>标签色，形如 #2563eb。</summary>
    public string Color { get; set; } = "#2563eb";

    /// <summary>所属账户：标签只在同账户的邮件上可用。</summary>
    public long AccountId { get; set; }

    /// <summary>单键快捷键（1-9 / A-Z / F1-F12）；null 或空表示未绑定。</summary>
    public string? ShortcutKey { get; set; }

    public int SortOrder { get; set; }

    /// <summary>侧栏 / 列表里展示的键位文本；未绑定返回空串。</summary>
    public string ShortcutDisplay => TagShortcutKeys.Normalize(ShortcutKey) ?? "";

    public bool HasShortcut => ShortcutDisplay.Length > 0;

    /// <summary>该键是否就是本标签绑定的单键。</summary>
    public bool MatchesShortcut(string? key)
    {
        var own = TagShortcutKeys.Normalize(ShortcutKey);
        var other = TagShortcutKeys.Normalize(key);
        return own is not null && other is not null && string.Equals(own, other, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>待办：把一封邮件变成需要跟进的事项，完成或忽略后归档。</summary>
public sealed class MailTodo
{
    public long Id { get; set; }

    public long EmailId { get; set; }

    public string Title { get; set; } = "";

    /// <summary>截止时间；null 表示无期限。</summary>
    public DateTimeOffset? DueUtc { get; set; }

    public MailTodoState State { get; set; } = MailTodoState.Open;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedUtc { get; set; }

    /// <summary>上次到期提醒时间；用于避免同一待办反复弹托盘气泡。</summary>
    public DateTimeOffset? RemindedUtc { get; set; }

    public bool IsOpen => State == MailTodoState.Open;

    public bool IsOverdue => State == MailTodoState.Open && DueUtc is { } due && due < DateTimeOffset.Now;

    public string DueDisplay => DueUtc is { } due
        ? (IsOverdue ? $"已逾期 · {DisplayFormat.FullDate(due)}" : $"截止 {DisplayFormat.FullDate(due)}")
        : "无截止时间";

    public string StateDisplay => State switch
    {
        MailTodoState.Open => IsOverdue ? "已逾期" : "待办",
        MailTodoState.Done => "已完成",
        _ => "已忽略"
    };
}

public enum MailTodoState
{
    Open = 0,
    Done = 1,
    Ignored = 2
}
