using ZungMail.Core.Domain;
using ZungMail.Mail;
using MimeKit;
using Xunit;

namespace ZungMail.Tests;

/// <summary>阅读收条（MDN）报文组装：结构、头与回退规则。</summary>
[Trait("Category", "Unit")]
public sealed class ReadReceiptTests
{
    private static Account Account() => new()
    {
        Email = "me@example.com",
        DisplayName = "我",
        Username = "me@example.com",
        ImapHost = "imap.example.com",
        SmtpHost = "smtp.example.com"
    };

    private static EmailDetail Original(string mdnTo) => new()
    {
        Id = 7,
        AccountId = 1,
        FolderId = 1,
        MessageId = "<abc@example.com>",
        Subject = "订舱确认",
        FromEmail = "sender@example.com",
        MdnTo = mdnTo
    };

    [Fact]
    public void BuildReceipt_生成multipartReport结构()
    {
        var message = ReadReceiptService.BuildReceipt(Account(), Original("sender@example.com"));

        Assert.Equal("已读回执：订舱确认", message.Subject);
        Assert.Equal("auto-replied", message.Headers["Auto-Submitted"]);
        Assert.Equal("abc@example.com", message.InReplyTo);

        var report = Assert.IsType<Multipart>(message.Body);
        Assert.Equal("report", report.ContentType.MediaSubtype);
        Assert.Equal("disposition-notification", report.ContentType.Parameters["report-type"]);
        Assert.Equal(2, report.Count);

        var machine = Assert.IsType<MimePart>(report[1]);
        Assert.Equal("message", machine.ContentType.MediaType);
        Assert.Equal("disposition-notification", machine.ContentType.MediaSubtype);

        var content = Assert.IsAssignableFrom<MimeContent>(machine.Content);
        using var reader = new StreamReader(content.Open());
        var fields = reader.ReadToEnd();
        Assert.Contains("Disposition: manual-action/MDN-sent-manually; displayed", fields);
        Assert.Contains("Original-Message-ID: <abc@example.com>", fields);
        Assert.Contains("Final-Recipient: rfc822; me@example.com", fields);
    }

    [Fact]
    public void BuildReceipt_带回显名的收条地址_解析出邮箱()
    {
        var message = ReadReceiptService.BuildReceipt(Account(), Original("\"发件人\" <sender@example.com>"));

        var to = Assert.IsType<MailboxAddress>(message.To[0]);
        Assert.Equal("sender@example.com", to.Address);
    }

    [Fact]
    public void BuildReceipt_收条地址不规范_回退到账户邮箱()
    {
        var message = ReadReceiptService.BuildReceipt(Account(), Original("###不是地址###"));

        var to = Assert.IsType<MailboxAddress>(message.To[0]);
        Assert.Equal("me@example.com", to.Address);
    }
}
