using ZungMail.Core.Domain;
using ZungMail.Mail;
using MimeKit;
using Xunit;

namespace ZungMail.Tests;

/// <summary>发送链路 MIME 组装：编辑器产出的 HTML（含 data: 图片）必须落成 multipart/related + cid 引用。</summary>
[Trait("Category", "Unit")]
public sealed class SmtpSendMessageTests
{
    /// <summary>1×1 透明 PNG。</summary>
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";

    [Fact]
    public void BuildMessage_data图片转为multipart_related内嵌资源()
    {
        var item = NewItem($"<p>正文</p><p><img src=\"data:image/png;base64,{PngBase64}\"></p>");

        var message = SmtpSendService.BuildMessage(NewAccount(), item);

        // MimeKit 会产出 multipart/alternative(text/plain + multipart/related(text/html + 图片))
        var related = Assert.Single(Flatten(message.Body).OfType<MultipartRelated>());
        var image = Assert.Single(Flatten(related).OfType<MimePart>(), p => p.ContentType.MediaType == "image");

        Assert.Equal("image/png", image.ContentType.MimeType);
        Assert.Equal(ContentDisposition.Inline, image.ContentDisposition?.Disposition);
        Assert.False(string.IsNullOrWhiteSpace(image.ContentId));

        var html = Assert.Single(Flatten(related).OfType<TextPart>(), p => p.IsHtml);
        Assert.Contains($"cid:{image.ContentId}", html.Text);
        Assert.DoesNotContain("data:image", html.Text);
    }

    [Fact]
    public void BuildMessage_纯HTML正文同时生成纯文本备选()
    {
        var message = SmtpSendService.BuildMessage(NewAccount(), NewItem("<p>第一段</p><p>第二段</p>"));

        var alternative = Assert.IsType<MultipartAlternative>(message.Body);
        var html = alternative.OfType<TextPart>().Single(p => p.IsHtml);
        var plain = alternative.OfType<TextPart>().Single(p => !p.IsHtml);

        Assert.Contains("<p>第一段</p>", html.Text);
        Assert.Contains("第一段", plain.Text);
        Assert.Contains("第二段", plain.Text);
    }

    [Fact]
    public void BuildMessage_使用入队时固定的MessageId()
    {
        var item = NewItem("<p>hi</p>");
        item.MessageId = "abc123@example.com";

        var message = SmtpSendService.BuildMessage(NewAccount(), item);

        Assert.Equal("abc123@example.com", message.MessageId);
    }

    [Fact]
    public void BuildMessage_收件人与回复线索正确写入()
    {
        var item = NewItem("<p>hi</p>");
        item.Cc.Add(new EmailAddress("cc@example.com"));
        item.InReplyTo = "<origin@example.com>";
        item.References = "<a@example.com> <origin@example.com>";

        var message = SmtpSendService.BuildMessage(NewAccount(), item);

        Assert.Equal("to@example.com", Assert.Single(message.To.Mailboxes).Address);
        Assert.Equal("cc@example.com", Assert.Single(message.Cc.Mailboxes).Address);
        Assert.Equal("origin@example.com", message.InReplyTo);
        Assert.Equal(new[] { "a@example.com", "origin@example.com" }, message.References);
    }

    /// <summary>深度遍历 MIME 树（MimeEntity 自身也在结果中）。</summary>
    private static IEnumerable<MimeEntity> Flatten(MimeEntity? entity)
    {
        if (entity is null) yield break;
        yield return entity;
        if (entity is not Multipart multipart) yield break;
        foreach (var child in multipart)
        {
            foreach (var item in Flatten(child)) yield return item;
        }
    }

    private static Account NewAccount() => new()
    {
        Name = "测试账户",
        Email = "me@example.com",
        Username = "me@example.com",
        ImapHost = "imap.example.com",
        SmtpHost = "smtp.example.com"
    };

    private static OutboxItem NewItem(string html) => new()
    {
        Subject = "富文本编辑器验证",
        BodyHtml = html,
        IsHtml = true,
        To = { new EmailAddress("to@example.com") }
    };
}
