using ZungMail.Core.Domain;
using ZungMail.Mail;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 后台轮询策略：收件箱每轮必刷、其余文件夹按轮次滚动、正文预取只给收件箱。
/// 这几条决定了「设 1 分钟同步」到底能不能接近 1 分钟，所以用纯函数锁住行为。
/// </summary>
[Trait("Category", "Unit")]
public sealed class BackgroundSyncPolicyTests
{
    private sealed record Folder(long Id, string Name, FolderKind Kind);

    private static List<Folder> MakeFolders(int count, FolderKind kind = FolderKind.None)
        => Enumerable.Range(1, count).Select(i => new Folder(i, $"F{i}", kind)).ToList();

    [Fact]
    public void 文件夹列表只在首轮整倍数和全量重同步时刷新()
    {
        Assert.True(BackgroundSyncPolicy.ShouldRefreshFolderList(round: 1, fullResync: false));
        Assert.True(BackgroundSyncPolicy.ShouldRefreshFolderList(round: BackgroundSyncPolicy.FolderListRefreshRounds, fullResync: false));
        Assert.False(BackgroundSyncPolicy.ShouldRefreshFolderList(round: 2, fullResync: false));
        Assert.True(BackgroundSyncPolicy.ShouldRefreshFolderList(round: 2, fullResync: true));
    }

    [Fact]
    public void 收件箱永远排在本轮第一位()
    {
        var folders = MakeFolders(6);
        folders.Add(new Folder(99, "INBOX", FolderKind.Inbox));

        var plan = BackgroundSyncPolicy.SelectFoldersForRound(folders, round: 3, folder => folder.Kind);

        Assert.Equal(FolderKind.Inbox, plan[0].Kind);
        Assert.Equal("INBOX", plan[0].Name);
    }

    [Fact]
    public void 冷文件夹少于每轮配额时全部纳入()
    {
        var folders = MakeFolders(3);

        var plan = BackgroundSyncPolicy.SelectFoldersForRound(
            folders, round: 1, folder => folder.Kind, coldPerRound: BackgroundSyncPolicy.ColdFoldersPerRound);

        Assert.Equal(3, plan.Count);
    }

    [Fact]
    public void 每轮只取配额数量的冷文件夹()
    {
        var folders = MakeFolders(50);

        var plan = BackgroundSyncPolicy.SelectFoldersForRound(folders, round: 1, folder => folder.Kind);

        Assert.Equal(BackgroundSyncPolicy.ColdFoldersPerRound, plan.Count);
    }

    [Fact]
    public void 多轮滚动之后每个文件夹都会被覆盖到()
    {
        var folders = MakeFolders(50);
        var visited = new HashSet<long>();

        // 50 个冷文件夹 / 每轮 20 个 → 3 轮足够覆盖全部
        for (var round = 1; round <= 3; round++)
        {
            foreach (var folder in BackgroundSyncPolicy.SelectFoldersForRound(folders, round, item => item.Kind))
            {
                visited.Add(folder.Id);
            }
        }

        Assert.Equal(50, visited.Count);
    }

    [Fact]
    public void 相邻两轮不会重复刷同一批冷文件夹()
    {
        var folders = MakeFolders(50);

        var first = BackgroundSyncPolicy.SelectFoldersForRound(folders, round: 1, item => item.Kind).Select(item => item.Id).ToHashSet();
        var second = BackgroundSyncPolicy.SelectFoldersForRound(folders, round: 2, item => item.Kind).Select(item => item.Id).ToHashSet();

        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void 正文预取只在前台全量或收件箱时开启()
    {
        Assert.True(BackgroundSyncPolicy.ShouldPrefetchBodies(SyncScope.All, FolderKind.Sent));
        Assert.True(BackgroundSyncPolicy.ShouldPrefetchBodies(SyncScope.Incremental, FolderKind.Inbox));
        Assert.False(BackgroundSyncPolicy.ShouldPrefetchBodies(SyncScope.Incremental, FolderKind.Sent));
        Assert.False(BackgroundSyncPolicy.ShouldPrefetchBodies(SyncScope.Incremental, FolderKind.None));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(500, 120)]
    public void 同步间隔夹取到一分钟到两小时(int minutes, int expected)
    {
        Assert.Equal(expected, AppSettings.EffectiveSyncIntervalMinutes(minutes));
    }
}
