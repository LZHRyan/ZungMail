using MimeKit;
using ZungMail.Mail;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 退信细节解析：从 message/delivery-status 段取失败收件人、状态码与诊断；
/// 不是 DSN、只有延迟、缺字段时返回 null，绝不抛异常。
/// </summary>
[Trait("Category", "Unit")]
public sealed class BounceDsnParserTests
{
    [Fact]
    public void 解析出失败收件人状态码与诊断()
    {
        var message = BuildDsn(
            action: "failed",
            status: "5.1.1",
            diagnostic: "smtp; 550 5.1.1 User unknown");

        var details = BounceDsnParser.Parse(message);

        Assert.NotNull(details);
        Assert.Equal("ops@forwarder.cn", details!.Recipient);
        Assert.Equal("5.1.1", details.Status);
        Assert.Equal("550 5.1.1 User unknown", details.Detail);
    }

    [Fact]
    public void 诊断码折行被压成一行()
    {
        var message = BuildDsn(
            action: "failed",
            status: "5.2.2",
            diagnostic: "smtp; 552 5.2.2 Mailbox full\r\n (quota exceeded)");

        var details = BounceDsnParser.Parse(message);

        Assert.NotNull(details);
        Assert.Equal("552 5.2.2 Mailbox full (quota exceeded)", details!.Detail);
    }

    [Fact]
    public void 没有Action但状态码是5xx也算()
    {
        var details = BounceDsnParser.Parse(BuildDsn(action: "", status: "5.7.1"));

        Assert.NotNull(details);
        Assert.Equal("5.7.1", details!.Status);
    }

    [Fact]
    public void 只有延迟不算失败()
    {
        Assert.Null(BounceDsnParser.Parse(BuildDsn(action: "delayed", status: "4.2.2")));
    }

    [Fact]
    public void 普通邮件不是退信()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("客户", "ops@forwarder.cn"));
        message.To.Add(new MailboxAddress("", "me@example.com"));
        message.Subject = "关于退信的处理";
        message.Body = new TextPart("plain") { Text = "你好" };

        Assert.Null(BounceDsnParser.Parse(message));
    }

    [Fact]
    public void 缺收件人时仍返回状态码()
    {
        var details = BounceDsnParser.Parse(BuildDsn(action: "failed", status: "5.1.10", recipient: ""));

        Assert.NotNull(details);
        Assert.Equal("", details!.Recipient);
        Assert.Equal("5.1.10", details.Status);
    }

    private static MimeMessage BuildDsn(
        string action,
        string status,
        string? diagnostic = null,
        string recipient = "ops@forwarder.cn")
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Mail Delivery System", "MAILER-DAEMON@example.com"));
        message.To.Add(new MailboxAddress("", "me@example.com"));
        message.Subject = "Undelivered Mail Returned to Sender";

        var report = new MultipartReport("delivery-status");

        var human = new TextPart("plain") { Text = "This is the mail system at host mx.example.com." };

        var deliveryStatus = new MessageDeliveryStatus();
        var group = new HeaderList();
        group.Add("Reporting-MTA", "dns; mx.example.com");
        if (recipient.Length > 0) group.Add("Final-Recipient", "rfc822; " + recipient);
        if (action.Length > 0) group.Add("Action", action);
        if (status.Length > 0) group.Add("Status", status);
        if (!string.IsNullOrEmpty(diagnostic)) group.Add("Diagnostic-Code", diagnostic);
        deliveryStatus.StatusGroups.Add(group);

        var original = new MimeMessage();
        original.From.Add(new MailboxAddress("我", "me@example.com"));
        original.To.Add(new MailboxAddress("", "ops@forwarder.cn"));
        original.Subject = "回复：1030954 / MBL";
        original.Body = new TextPart("plain") { Text = "报价见附件" };

        report.Add(human);
        report.Add(deliveryStatus);
        report.Add(new MessagePart { Message = original });

        message.Body = report;
        return message;
    }
}
