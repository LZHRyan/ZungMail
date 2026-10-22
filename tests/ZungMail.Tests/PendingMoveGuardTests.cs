using ZungMail.Application.Services;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 「移动」类待同步操作的重试前置判断：本地已生效的项不能再回写服务器，
/// 否则会撞上「源文件夹 == 目标文件夹」永远失败，变成每次同步都重试的僵尸计数。
/// </summary>
[Trait("Category", "Unit")]
public sealed class PendingMoveGuardTests
{
    [Fact]
    public void 本地行已不存在时直接结案()
    {
        var outcome = PendingMoveGuard.Evaluate(
            messageExists: false,
            localFolderId: 0,
            localFolderKind: null,
            destinationFolderId: 0,
            destinationKind: FolderKind.Trash);

        Assert.Equal(PendingMoveOutcome.AlreadyApplied, outcome);
    }

    [Fact]
    public void 邮件已在目标语义文件夹时结案()
    {
        // 这条就是历史 bug：本地已经在垃圾箱，重试时源 == 目标，服务端回写永远失败
        var outcome = PendingMoveGuard.Evaluate(
            messageExists: true,
            localFolderId: 1477,
            localFolderKind: FolderKind.Trash,
            destinationFolderId: 0,
            destinationKind: FolderKind.Trash);

        Assert.Equal(PendingMoveOutcome.AlreadyApplied, outcome);
    }

    [Fact]
    public void 邮件还在别的语义文件夹时需要回写()
    {
        var outcome = PendingMoveGuard.Evaluate(
            messageExists: true,
            localFolderId: 1474,
            localFolderKind: FolderKind.Inbox,
            destinationFolderId: 0,
            destinationKind: FolderKind.Trash);

        Assert.Equal(PendingMoveOutcome.Push, outcome);
    }

    [Fact]
    public void 邮件已在目标自定义文件夹时结案()
    {
        var outcome = PendingMoveGuard.Evaluate(
            messageExists: true,
            localFolderId: 1498,
            localFolderKind: FolderKind.None,
            destinationFolderId: 1498,
            destinationKind: FolderKind.None);

        Assert.Equal(PendingMoveOutcome.AlreadyApplied, outcome);
    }

    [Fact]
    public void 邮件还在原文件夹时需要回写自定义目标()
    {
        var outcome = PendingMoveGuard.Evaluate(
            messageExists: true,
            localFolderId: 1483,
            localFolderKind: FolderKind.None,
            destinationFolderId: 1498,
            destinationKind: FolderKind.None);

        Assert.Equal(PendingMoveOutcome.Push, outcome);
    }

    [Theory]
    [InlineData(FolderKind.Inbox, "收件箱")]
    [InlineData(FolderKind.Trash, "垃圾箱")]
    [InlineData(FolderKind.Junk, "垃圾邮件")]
    [InlineData(FolderKind.Archive, "邮件归档")]
    [InlineData(FolderKind.None, "文件夹")]
    public void 语义文件夹中文名(FolderKind kind, string expected)
        => Assert.Equal(expected, PendingMoveGuard.KindLabel(kind));
}
