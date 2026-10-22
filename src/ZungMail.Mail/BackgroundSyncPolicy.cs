using ZungMail.Core.Domain;

namespace ZungMail.Mail;

/// <summary>一次同步覆盖哪些文件夹。</summary>
public enum SyncScope
{
    /// <summary>前台（手动刷新 / 设置里点同步）：顺序遍历全部可选文件夹，正文预取也全开。</summary>
    All = 0,

    /// <summary>后台轮询：收件箱必刷 + 其余文件夹滚动遍历 + 单轮时间预算。</summary>
    Incremental = 1,

    /// <summary>只刷收件箱：IMAP IDLE 探测到新邮件后的即时同步。</summary>
    InboxOnly = 2
}

/// <summary>
/// 后台轮询策略。把「每轮遍历全部文件夹」改成「收件箱每轮必刷 + 其余文件夹滚动推进」，
/// 并给单轮设时间预算，避免一轮拖到几分钟、把 1 分钟间隔变成「轮次耗时 + 1 分钟」。
/// </summary>
public static class BackgroundSyncPolicy
{
    /// <summary>单轮里收件箱之外最多访问多少个文件夹（按轮次滚动，保证每个都会被覆盖）。</summary>
    public const int ColdFoldersPerRound = 20;

    /// <summary>后台单轮的时间预算：用满即收尾，剩下的文件夹下一轮接着来。</summary>
    public static readonly TimeSpan RoundBudget = TimeSpan.FromSeconds(45);

    /// <summary>文件夹列表（LIST）每隔多少轮刷新一次。</summary>
    public const int FolderListRefreshRounds = 12;

    /// <summary>本轮是否需要重新向服务器要文件夹列表（建删文件夹不会每分钟看一次）。</summary>
    public static bool ShouldRefreshFolderList(int round, bool fullResync)
        => fullResync || round <= 1 || round % FolderListRefreshRounds == 0;

    /// <summary>
    /// 本轮要同步的文件夹：收件箱永远排在最前面（保证新邮件第一时间可见），
    /// 其余按 <paramref name="round"/> 滚动取 <paramref name="coldPerRound"/> 个。
    /// </summary>
    public static List<T> SelectFoldersForRound<T>(
        IReadOnlyList<T> folders,
        int round,
        Func<T, FolderKind> kindOf,
        int coldPerRound = ColdFoldersPerRound)
    {
        var selected = new List<T>(folders.Count);
        var cold = new List<T>(folders.Count);

        foreach (var folder in folders)
        {
            if (kindOf(folder) == FolderKind.Inbox) selected.Add(folder);
            else cold.Add(folder);
        }

        if (cold.Count == 0) return selected;

        if (coldPerRound <= 0 || coldPerRound >= cold.Count)
        {
            selected.AddRange(cold);
            return selected;
        }

        // 起点随轮次推进：多轮下来每个文件夹都会被轮到，尾部文件夹不会永远排在预算之外
        var offset = (int)((Math.Max(round, 1) - 1) * (long)coldPerRound % cold.Count);
        for (var i = 0; i < coldPerRound; i++) selected.Add(cold[(offset + i) % cold.Count]);

        return selected;
    }

    /// <summary>
    /// 只有收件箱（以及前台全量同步）才预取正文：正文预取是单轮里最贵的操作，
    /// 让每个文件夹都预取会把一轮拖长几倍，而冷文件夹的正文按需下载即可。
    /// </summary>
    public static bool ShouldPrefetchBodies(SyncScope scope, FolderKind kind)
        => scope == SyncScope.All || kind == FolderKind.Inbox;
}
