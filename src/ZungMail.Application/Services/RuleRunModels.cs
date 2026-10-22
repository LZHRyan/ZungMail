namespace ZungMail.Application.Services;

/// <summary>「对已有邮件执行一次规则」的进度快照。</summary>
public sealed record RuleRunProgress(int Scanned, int Matched, int FolderCount, string Stage);

/// <summary>「执行一次」的结果：扫了多少、命中并执行了多少、失败多少。</summary>
public sealed record RuleRunResult(
    int Scanned,
    int Matched,
    int Failed,
    int FolderCount,
    bool ReachedLimit,
    RuleRunScope Scope)
{
    public static RuleRunResult Empty(RuleRunScope scope) => new(0, 0, 0, 0, false, scope);

    /// <summary>给用户看的一句话结论。</summary>
    public string Summary
    {
        get
        {
            if (FolderCount == 0) return "没有可扫描的文件夹（收件箱为空或尚未同步）。";

            var scopeText = Scope == RuleRunScope.Inbox ? "收件箱" : "所有文件夹";
            var text = $"已扫描{scopeText} {Scanned} 封，命中并执行 {Matched} 封";
            if (Failed > 0) text += $"，失败 {Failed} 封";
            if (ReachedLimit) text += "（已达单次上限，可再次执行继续处理）";
            return text + "。";
        }
    }
}
