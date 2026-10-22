namespace ZungMail.Core.Domain;

/// <summary>联系人（FR-10），以邮箱地址为唯一键去重。</summary>
public sealed class Contact
{
    public long Id { get; set; }

    /// <summary>唯一键，小写规范化。</summary>
    public string Email { get; set; } = "";

    public string Name { get; set; } = "";
    public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;
    public int UseCount { get; set; }

    /// <summary>白名单：垃圾邮件误判时加入，避免再次被拦截。</summary>
    public bool IsWhitelisted { get; set; }

    public string Display => string.IsNullOrWhiteSpace(Name) ? Email : $"{Name} <{Email}>";
    public string Initial => DisplayFormat.Initial(string.IsNullOrWhiteSpace(Name) ? Email : Name);
    public string AvatarColor => DisplayFormat.ColorFor(Email);
}

/// <summary>附件元数据（FR-07）。</summary>
public sealed class AttachmentInfo
{
    public long Id { get; set; }
    public long EmailId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public bool IsInline { get; set; }

    /// <summary>本地缓存路径（下载后填充）。</summary>
    public string? LocalPath { get; set; }

    public string SizeDisplay => DisplayFormat.FileSize(Size);
    public bool IsDownloaded => !string.IsNullOrEmpty(LocalPath) && File.Exists(LocalPath);
}

/// <summary>模板（FR-08），以 JSON 文件持久化。</summary>
public sealed class MailTemplate
{
    public const string DefaultCategory = "通用";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary>模板说明（可选），列表副标题与编辑区提示。</summary>
    public string Description { get; set; } = "";

    /// <summary>分组名称，用于列表筛选。</summary>
    public string Category { get; set; } = DefaultCategory;

    /// <summary>内置模板：允许编辑但不可删除，可一键恢复默认内容。</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// 快速文本：撰写窗口侧栏的短话术，双击追加到正文光标处。
    /// 与普通模板共用同一份 JSON 存储与设置页编辑器，但不进入撰写窗口的「模板」下拉。
    /// </summary>
    public bool IsQuickText { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>主题展示串（空主题回退为占位提示）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? "（无主题）" : Subject;

    [System.Text.Json.Serialization.JsonIgnore]
    public string UpdatedDisplay => DisplayFormat.FullDate(UpdatedUtc);

    /// <summary>复制模板；<paramref name="newId"/> 为 true 时生成新标识（用于「另存为副本」）。</summary>
    public MailTemplate Clone(bool newId = false) => new()
    {
        Id = newId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        Subject = Subject,
        Body = Body,
        Description = Description,
        Category = Category,
        IsBuiltIn = newId ? false : IsBuiltIn,
        IsQuickText = IsQuickText,
        UpdatedUtc = UpdatedUtc
    };
}

/// <summary>待发送队列条目（FR-08 定时发送）。</summary>
public sealed class OutboxItem
{
    /// <summary>单条邮件自动投递的最大尝试次数；用尽后停止自动重试，可由用户在发件箱手动重试。</summary>
    public const int MaxAttempts = 5;

    public long Id { get; set; }
    public long AccountId { get; set; }

    /// <summary>
    /// 入队时生成的稳定 Message-ID（不含尖括号）。发送与归档复用同一值，
    /// 便于服务器「已发送」去重，也便于投递中断后人工核对。
    /// </summary>
    public string MessageId { get; set; } = "";

    public List<EmailAddress> To { get; set; } = new();
    public List<EmailAddress> Cc { get; set; } = new();
    public List<EmailAddress> Bcc { get; set; } = new();

    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public bool IsHtml { get; set; } = true;
    public List<string> AttachmentPaths { get; set; } = new();

    /// <summary>为 null 表示立即发送；否则到点发送。</summary>
    public DateTimeOffset? ScheduledUtc { get; set; }

    public OutboxState State { get; set; } = OutboxState.Pending;
    public string? LastError { get; set; }
    public int Attempts { get; set; }

    /// <summary>
    /// 下一次自动投递的最早时间（失败退避）；null 表示不受退避限制。
    /// 用于避免后台轮询在极短时间内把 <see cref="MaxAttempts"/> 次重试全部耗尽。
    /// </summary>
    public DateTimeOffset? NextAttemptUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string? InReplyTo { get; set; }
    public string? References { get; set; }

    public string StateDisplay => State switch
    {
        OutboxState.Pending => "待发送",
        OutboxState.Sending => "发送中",
        OutboxState.Sent => "已发送",
        OutboxState.Uncertain => "发送结果未知",
        _ => "发送失败"
    };

    /// <summary>是否为「已提交但结果未知」：界面需与可安全重试的失败区分呈现。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUncertain => State == OutboxState.Uncertain;

    public string ScheduleDisplay => ScheduledUtc is null
        ? "立即发送"
        : DisplayFormat.FullDate(ScheduledUtc.Value);

    /// <summary>
    /// 失败后的自动重试等待时长（指数退避，上限 30 分钟）：第 1 次失败等 1 分钟，第 2 次 2 分钟，以此类推。
    /// </summary>
    public static TimeSpan RetryDelay(int attempts)
    {
        var minutes = Math.Min(Math.Pow(2, Math.Max(attempts - 1, 0)), 30);
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>投递尝试次数展示。</summary>
    public string AttemptsDisplay => State switch
    {
        OutboxState.Sent => "已成功投递",
        OutboxState.Sending => "正在投递…",
        OutboxState.Uncertain => "服务器可能已接受 · 重试前请确认「已发送」",
        OutboxState.Failed when Attempts >= MaxAttempts => $"已尝试 {Attempts}/{MaxAttempts} 次 · 需手动重试",
        OutboxState.Failed when NextAttemptUtc is { } next => $"已失败 {Attempts}/{MaxAttempts} 次 · 下次自动重试 {DisplayFormat.FullDate(next)}",
        _ => Attempts == 0 ? "尚未尝试投递" : $"已尝试 {Attempts}/{MaxAttempts} 次"
    };

    /// <summary>是否可由用户手动重试（已成功发送的不再重试）。</summary>
    public bool CanRetry => State != OutboxState.Sent;

    /// <summary>
    /// 生成稳定 Message-ID（不含尖括号）。优先使用发件域，缺失时回落到本地域；
    /// 唯一性由 GUID 保证，域不要求可解析。
    /// </summary>
    public static string NewMessageId(string? senderEmail)
    {
        var at = senderEmail?.LastIndexOf('@') ?? -1;
        var domain = at >= 0 && at < senderEmail!.Length - 1 ? senderEmail[(at + 1)..] : "zungmail.local";
        return $"{Guid.NewGuid():N}@{domain}";
    }

    /// <summary>收件人摘要（含抄送）。</summary>
    public string RecipientsDisplay => string.Join("; ", To.Concat(Cc).Select(a => a.Display));

    public string SubjectDisplay => string.IsNullOrWhiteSpace(Subject) ? "（无主题）" : Subject;
}

/// <summary>
/// 待同步操作：本地优先写操作的持久化意图。
/// <para>
/// 写入本地库后立即入队并尝试回写服务器；成功后置 <see cref="PendingOperationState.Succeeded"/>，
/// 失败置 <see cref="PendingOperationState.Failed"/> 并保留原因，供用户重试或撤销。
/// 这样即使离线或服务器拒绝，删除等操作也不会在下次同步时被静默「复活」。
/// </para>
/// </summary>
public sealed class PendingOperation
{
    public long Id { get; set; }

    /// <summary>目标邮件；批量操作为 0，明细见 <see cref="Payload"/>。</summary>
    public long EmailId { get; set; }

    public PendingOperationKind Kind { get; set; }

    public PendingOperationState State { get; set; } = PendingOperationState.Pending;

    /// <summary>期望回写的完整标志位。</summary>
    public MessageFlags Flags { get; set; }

    /// <summary>操作附加数据：删除操作保存邮件快照（便于撤销），批量已读保存 id 与标志位列表。</summary>
    public string? Payload { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>是否仍需要处理（未成功）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOutstanding => State != PendingOperationState.Succeeded;

    [System.Text.Json.Serialization.JsonIgnore]
    public string KindDisplay => Kind switch
    {
        PendingOperationKind.MarkSeen => "标记已读",
        PendingOperationKind.Flagged => "星标",
        PendingOperationKind.Deleted => "永久删除",
        PendingOperationKind.Move => "移动邮件",
        _ => "批量已读"
    };

    /// <summary>状态提示文案（状态栏 / 列表徽标）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StateDisplay => State switch
    {
        PendingOperationState.Pending => "等待同步",
        PendingOperationState.Syncing => "同步中",
        PendingOperationState.Succeeded => "已同步",
        _ => "同步失败"
    };
}

/// <summary>轻量记事本（FR-11，P2）。</summary>
public sealed class Note
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public string UpdatedDisplay => DisplayFormat.FullDate(UpdatedUtc);
    public string Preview => DisplayFormat.Preview(Content, 90);
}
