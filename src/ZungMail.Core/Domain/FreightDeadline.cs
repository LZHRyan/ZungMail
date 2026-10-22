namespace ZungMail.Core.Domain;

/// <summary>
/// 货代时效节点类型。海运链路里真正决定「能不能上船」的不是邮件主题，
/// 而是截关 / 截补料 / 截 VGM 这几个时间点，以及 ETD / ETA 这两个航程节点。
/// </summary>
public enum FreightDeadlineKind
{
    /// <summary>截关（未细分叫法，如 CUT OFF / 截止）。</summary>
    CutOff = 0,

    /// <summary>截补料（SI Cut-off）。</summary>
    SiCutOff = 1,

    /// <summary>截 VGM（VGM Cut-off）。</summary>
    VgmCutOff = 2,

    /// <summary>截单证（DOC Cut-off）。</summary>
    DocCutOff = 3,

    /// <summary>CY 截关 / 截港（CY Cut-off / Closing）。</summary>
    CyCutOff = 4,

    /// <summary>预计开船（ETD）。</summary>
    Etd = 5,

    /// <summary>预计到港（ETA）。</summary>
    Eta = 6
}

/// <summary>时效紧迫度：用于看板与详情卡的颜色分级。</summary>
public enum DeadlineUrgency
{
    /// <summary>已超时。</summary>
    Overdue = 0,

    /// <summary>6 小时内到期，必须马上处理。</summary>
    Critical = 1,

    /// <summary>48 小时内到期。</summary>
    Soon = 2,

    /// <summary>还有充足时间。</summary>
    Later = 3
}

/// <summary>
/// 从邮件中抽取出的一个时效节点。<see cref="DueUtc"/> 统一按 UTC 存储，
/// 展示时转本地时间（解析口径见 <see cref="Services.FreightDeadlineExtractor"/>）。
/// </summary>
public sealed class FreightDeadline
{
    public FreightDeadlineKind Kind { get; init; }

    /// <summary>到期时间（UTC）。</summary>
    public DateTimeOffset DueUtc { get; init; }

    /// <summary>命中的原文片段，用于在界面上回看「这句话是从哪来的」。</summary>
    public string RawText { get; init; } = "";

    /// <summary>抽取来源：主题里的节点通常比正文更重要。</summary>
    public ReferenceSource Source { get; init; }

    /// <summary>是否为截关类节点（截关 / 截补料 / VGM / 单证 / CY）。</summary>
    public bool IsCutOff => Kind <= FreightDeadlineKind.CyCutOff;

    public string KindLabel => LabelOf(Kind);

    /// <summary>标签用的短名，例如「SI」「VGM」「ETD」。</summary>
    public string KindShort => ShortOf(Kind);

    /// <summary>节点主色：截关类偏警示，ETD / ETA 偏航程信息。</summary>
    public string KindColor => ColorOf(Kind);

    /// <summary>完整到期时间（本地时区）。</summary>
    public string DueDisplay => DisplayFormat.FullDate(DueUtc);

    /// <summary>到期日（本地时区），用于看板第二行。</summary>
    public string DueDayDisplay => DueUtc.ToLocalTime().ToString("MM-dd HH:mm");

    public DeadlineUrgency Urgency(DateTimeOffset now)
    {
        var remaining = DueUtc - now;
        if (remaining <= TimeSpan.Zero) return DeadlineUrgency.Overdue;
        if (remaining <= TimeSpan.FromHours(6)) return DeadlineUrgency.Critical;
        if (remaining <= TimeSpan.FromHours(48)) return DeadlineUrgency.Soon;
        return DeadlineUrgency.Later;
    }

    /// <summary>「距截关 4 小时」/「已超时 2 小时」。</summary>
    public string Countdown(DateTimeOffset now)
    {
        var remaining = DueUtc - now;
        var overdue = remaining <= TimeSpan.Zero;
        var span = overdue ? remaining.Negate() : remaining;

        string amount;
        if (span.TotalDays >= 1)
        {
            amount = $"{(int)span.TotalDays} 天";
        }
        else if (span.TotalHours >= 1)
        {
            amount = $"{(int)span.TotalHours} 小时";
        }
        else
        {
            amount = $"{Math.Max(1, (int)span.TotalMinutes)} 分钟";
        }

        return overdue ? $"已超时 {amount}" : $"距{KindLabel} {amount}";
    }

    /// <summary>紧迫度对应的颜色（与主题调色板一致）。</summary>
    public static string UrgencyColor(DeadlineUrgency urgency) => urgency switch
    {
        DeadlineUrgency.Overdue => "#dc2626",
        DeadlineUrgency.Critical => "#ea580c",
        DeadlineUrgency.Soon => "#d97706",
        _ => "#0d9488"
    };

    public static string LabelOf(FreightDeadlineKind kind) => kind switch
    {
        FreightDeadlineKind.SiCutOff => "截补料",
        FreightDeadlineKind.VgmCutOff => "截 VGM",
        FreightDeadlineKind.DocCutOff => "截单证",
        FreightDeadlineKind.CyCutOff => "CY 截关",
        FreightDeadlineKind.Etd => "开船",
        FreightDeadlineKind.Eta => "到港",
        _ => "截关"
    };

    public static string ShortOf(FreightDeadlineKind kind) => kind switch
    {
        FreightDeadlineKind.SiCutOff => "SI",
        FreightDeadlineKind.VgmCutOff => "VGM",
        FreightDeadlineKind.DocCutOff => "DOC",
        FreightDeadlineKind.CyCutOff => "CY",
        FreightDeadlineKind.Etd => "ETD",
        FreightDeadlineKind.Eta => "ETA",
        _ => "CUT"
    };

    public static string ColorOf(FreightDeadlineKind kind) => kind switch
    {
        FreightDeadlineKind.SiCutOff => "#dc2626",
        FreightDeadlineKind.VgmCutOff => "#c026d3",
        FreightDeadlineKind.DocCutOff => "#ea580c",
        FreightDeadlineKind.CyCutOff => "#b91c1c",
        FreightDeadlineKind.Etd => "#0ea5e9",
        FreightDeadlineKind.Eta => "#22c55e",
        _ => "#ef4444"
    };
}
