namespace ZungMail.Core.Domain;

/// <summary>规则命中后的动作。</summary>
public enum MailRuleAction
{
    /// <summary>移动到指定文件夹。</summary>
    MoveToFolder = 0,

    /// <summary>加星标。</summary>
    Star = 1,

    /// <summary>标记为已读。</summary>
    MarkSeen = 2,

    /// <summary>移动到归档文件夹。</summary>
    Archive = 3,

    /// <summary>标记为垃圾邮件。</summary>
    MarkJunk = 4,

    /// <summary>打标签（<see cref="MailRule.ActionArg"/> 为标签名）。</summary>
    AddTag = 5
}

/// <summary>
/// 本地邮件规则（过滤器）：条件为「发件人包含」「主题包含」的与关系，空条件表示不限。
/// 命中后执行一个动作，并记录命中次数与时间，便于回溯自动化是否生效。
/// </summary>
public sealed class MailRule
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>发件人地址或显示名包含的文本（不区分大小写）。</summary>
    public string FromContains { get; set; } = "";

    /// <summary>主题包含的文本（不区分大小写）。</summary>
    public string SubjectContains { get; set; } = "";

    /// <summary>发件域（@ 之后的部分）包含的文本，例如 <c>maersk.com</c>；用于「某船司/客户来信自动归类」。</summary>
    public string FromDomain { get; set; } = "";

    public MailRuleAction Action { get; set; } = MailRuleAction.Star;

    /// <summary>动作参数：打标签时为标签名；其余动作忽略。</summary>
    public string ActionArg { get; set; } = "";

    /// <summary>动作为移动到文件夹时的目标；其余动作忽略。</summary>
    public long? TargetFolderId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 命中后是否继续评估后续规则（对齐 Foxmail 过滤器的「继续执行后续过滤器」）。
    /// 关闭时沿用默认语义：标记类动作可叠加，移动类动作终止后续匹配。
    /// </summary>
    public bool ContinueAfterMatch { get; set; }

    public int HitCount { get; set; }

    public DateTimeOffset? LastHitUtc { get; set; }

    /// <summary>是否具备可执行的条件（至少一个条件非空）。</summary>
    public bool HasCondition =>
        !string.IsNullOrWhiteSpace(FromContains)
        || !string.IsNullOrWhiteSpace(FromDomain)
        || !string.IsNullOrWhiteSpace(SubjectContains);

    public string ConditionDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(FromContains)) parts.Add($"发件人含 “{FromContains}”");
            if (!string.IsNullOrWhiteSpace(FromDomain)) parts.Add($"发件域含 “{FromDomain}”");
            if (!string.IsNullOrWhiteSpace(SubjectContains)) parts.Add($"主题含 “{SubjectContains}”");
            return parts.Count == 0 ? "（无条件，不会命中）" : string.Join("，", parts);
        }
    }

    public string ActionDisplay => Action switch
    {
        MailRuleAction.MoveToFolder => "移动到指定文件夹",
        MailRuleAction.Star => "加星标",
        MailRuleAction.MarkSeen => "标记为已读",
        MailRuleAction.Archive => "归档",
        MailRuleAction.MarkJunk => "标记为垃圾邮件",
        MailRuleAction.AddTag => string.IsNullOrWhiteSpace(ActionArg) ? "打标签（未指定标签）" : $"打标签 “{ActionArg}”",
        _ => "未知动作"
    };

    public string HitDisplay => HitCount == 0
        ? "尚未命中"
        : $"已命中 {HitCount} 次 · 最近 {DisplayFormat.FullDate(LastHitUtc ?? DateTimeOffset.UtcNow)}";
}
