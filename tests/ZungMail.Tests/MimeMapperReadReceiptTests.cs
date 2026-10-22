using ZungMail.Mail;
using MailKit;
using ImapFlags = MailKit.MessageFlags;
using MimeKit;
using Xunit;

namespace ZungMail.Tests;

/// <summary>MDN（阅读收条）请求头的采集：同步头清单 + 映射到 EmailDetail。</summary>
[Trait("Category", "Unit")]
public sealed class MimeMapperReadReceiptTests
{
    [Fact]
    public void ExtraHeaders_包含DispositionNotificationTo()
        => Assert.Contains(HeaderId.DispositionNotificationTo, MimeMapper.ExtraHeaders);

    [Fact]
    public void ToDetail_映射收条请求地址()
    {
        var summary = new RemoteMessageSummary
        {
            Uid = 12,
            Flags = ImapFlags.None,
            Envelope = new Envelope
            {
                Subject = "请确认",
                Date = DateTimeOffset.UtcNow,
                From = { new MailboxAddress(string.Empty, "sender@example.com") }
            },
            MdnTo = "  sender@example.com  "
        };

        var detail = MimeMapper.ToDetail(1, 2, 12, summary);

        Assert.Equal("sender@example.com", detail.MdnTo);
    }

    [Fact]
    public void ToDetail_无收条请求时为空字符串()
    {
        var summary = new RemoteMessageSummary
        {
            Uid = 13,
            Flags = ImapFlags.None,
            Envelope = new Envelope { Subject = "普通邮件", Date = DateTimeOffset.UtcNow }
        };

        Assert.Equal(string.Empty, MimeMapper.ToDetail(1, 2, 13, summary).MdnTo);
    }
}
