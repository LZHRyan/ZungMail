using ZungMail.Application.Services;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 「对已有邮件执行一次」的扫描计划：收件箱范围只扫 Inbox，
/// 全文件夹范围跳过已发送 / 草稿 / 垃圾箱 / 已删除，并各有单次扫描上限。
/// </summary>
[Trait("Category", "Unit")]
public sealed class RuleRunPlannerTests
{
    private static MailFolder Folder(long id, FolderKind kind, string name)
        => new() { Id = id, Kind = kind, DisplayName = name, AccountId = 1 };

    private static readonly MailFolder[] Folders =
    {
        Folder(1, FolderKind.Inbox, "收件箱"),
        Folder(2, FolderKind.Sent, "已发送"),
        Folder(3, FolderKind.Drafts, "草稿箱"),
        Folder(4, FolderKind.Trash, "已删除"),
        Folder(5, FolderKind.Junk, "垃圾邮件"),
        Folder(6, FolderKind.Archive, "邮件归档"),
        Folder(7, FolderKind.None, "客户资料"),
        Folder(8, FolderKind.Inbox, "另一个账户收件箱")
    };

    [Fact]
    public void 收件箱范围只包含收件箱()
    {
        var selected = RuleRunPlanner.SelectFolders(Folders, RuleRunScope.Inbox);

        Assert.Equal(new long[] { 1, 8 }, selected.Select(f => f.Id));
    }

    [Fact]
    public void 全文件夹范围跳过已发送草稿垃圾箱与已删除()
    {
        var selected = RuleRunPlanner.SelectFolders(Folders, RuleRunScope.AllFolders);

        Assert.Equal(new long[] { 1, 6, 7, 8 }, selected.Select(f => f.Id));
    }

    [Fact]
    public void 空文件夹列表返回空()
    {
        Assert.Empty(RuleRunPlanner.SelectFolders(Array.Empty<MailFolder>(), RuleRunScope.AllFolders));
    }

    [Fact]
    public void 空引用不抛异常()
    {
        Assert.Empty(RuleRunPlanner.SelectFolders(null!, RuleRunScope.Inbox));
    }

    [Fact]
    public void 收件箱上限小于全文件夹上限()
    {
        Assert.Equal(500, RuleRunPlanner.LimitFor(RuleRunScope.Inbox));
        Assert.Equal(2000, RuleRunPlanner.LimitFor(RuleRunScope.AllFolders));
    }
}
