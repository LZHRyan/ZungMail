using ZungMail.Core.Domain;
using ZungMail.Mail;
using MailKit;
using MailKit.Net.Imap;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 「服务器上已经没有这封邮件」的判定：这类失败必须结案，不能留在待同步队列里反复重试
/// （实测线上有 32 条「移动到已处理」就是这么卡住的，每条每次启动都重连一次服务器）。
/// </summary>
[Trait("Category", "Unit")]
public sealed class ImapFailureTests
{
    [Fact]
    public void 邮件取不到时算已不在()
        => Assert.True(ImapFailure.IsMessageGone(
            new MessageNotFoundException("The IMAP server did not return the requested message.")));

    [Fact]
    public void 文件夹不存在时算已不在()
        => Assert.True(ImapFailure.IsMessageGone(new FolderNotFoundException("已处理", "其他文件夹/已处理")));

    [Fact]
    public void 网络异常仍可重试()
        => Assert.False(ImapFailure.IsMessageGone(new IOException("The read operation failed")));

    [Theory]
    [InlineData("The IMAP server replied to the 'MOVE' command with a 'NO' response: UID MOVE can't move those messages or to that name")]
    [InlineData("The IMAP server replied to the 'MOVE' command with a 'NO' response: Cannot move the message")]
    [InlineData("Invalid UIDs specified")]
    [InlineData("No such message")]
    [InlineData("The IMAP server replied to the 'MOVE' command with a 'NO' response: Mails not exist, maybe already deleted or filtered")]
    [InlineData("message not exist")]
    [InlineData("Message already deleted")]
    [InlineData("Not found")]
    public void MOVE被拒为搬不动时算已不在(string message)
        => Assert.True(ImapFailure.IsRejectionAboutMessage(message));

    [Theory]
    [InlineData("Over quota")]
    [InlineData("Mailbox is read-only")]
    [InlineData("")]
    [InlineData(null)]
    public void 其它NO仍然可重试(string? message)
        => Assert.False(ImapFailure.IsRejectionAboutMessage(message));

    [Fact]
    public void 三种结局互不相等()
    {
        // 结案判定的依据：只有 Retryable 才留在队列里重试
        Assert.NotEqual(MovePushResult.Moved, MovePushResult.Retryable);
        Assert.NotEqual(MovePushResult.MessageGone, MovePushResult.Moved);
        Assert.NotEqual(MovePushResult.MessageGone, MovePushResult.Retryable);
    }
}
