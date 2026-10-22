using ZungMail.Core.Domain;

namespace ZungMail.Application.Services;

/// <summary>「对已有邮件执行一次规则」的作用范围。</summary>
public enum RuleRunScope
{
    /// <summary>只跑收件箱（推荐：规则通常就是给收件箱里的新邮件分类用的）。</summary>
    Inbox = 0,

    /// <summary>跑所有「有来信」的文件夹，跳过已发送 / 草稿 / 垃圾箱 / 已删除。</summary>
    AllFolders = 1
}

/// <summary>
/// 「执行一次」的扫描计划：选哪些文件夹、最多扫多少封。
/// 纯逻辑，便于单测；真正的查询与动作由 <see cref="MailRuleService"/> 执行。
/// </summary>
public static class RuleRunPlanner
{
    /// <summary>收件箱范围最多扫描的封数。</summary>
    public const int InboxLimit = 500;

    /// <summary>全文件夹范围最多扫描的封数（所有文件夹合计）。</summary>
    public const int AllFoldersLimit = 2000;

    /// <summary>不可作为规则执行目标的语义文件夹：已发送是「我发出的」，草稿没发出去，垃圾箱/已删除无需整理。</summary>
    private static readonly HashSet<FolderKind> ExcludedKinds = new()
    {
        FolderKind.Sent,
        FolderKind.Drafts,
        FolderKind.Trash,
        FolderKind.Junk
    };

    /// <summary>按范围挑出要扫描的文件夹（保持仓库返回的顺序，便于结果稳定）。</summary>
    public static IReadOnlyList<MailFolder> SelectFolders(IEnumerable<MailFolder> folders, RuleRunScope scope)
    {
        var all = folders?.Where(f => f is not null).ToList() ?? new List<MailFolder>();
        if (all.Count == 0) return Array.Empty<MailFolder>();

        return scope switch
        {
            RuleRunScope.Inbox => all.Where(f => f.Kind == FolderKind.Inbox).ToList(),
            _ => all.Where(f => !ExcludedKinds.Contains(f.Kind)).ToList()
        };
    }

    /// <summary>该范围允许扫描的总封数上限。</summary>
    public static int LimitFor(RuleRunScope scope) => scope == RuleRunScope.Inbox ? InboxLimit : AllFoldersLimit;
}
