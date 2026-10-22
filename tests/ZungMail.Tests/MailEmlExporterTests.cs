using ZungMail.Core.Domain;
using ZungMail.Mail;
using MimeKit;
using Xunit;

namespace ZungMail.Tests;

/// <summary>「作为附件转发」：把库里的邮件重建成 .eml，收件人能用客户端直接打开。</summary>
[Trait("Category", "Unit")]
public sealed class MailEmlExporterTests : IDisposable
{
    private readonly string _root;

    public MailEmlExporterTests()
        => _root = Path.Combine(Path.GetTempPath(), "zungmail-eml-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task 导出_保留原邮件头正文与附件且文件名取主题()
    {
        Directory.CreateDirectory(_root);
        var attachmentPath = Path.Combine(_root, "报价单.pdf");
        await File.WriteAllBytesAsync(attachmentPath, new byte[] { 1, 2, 3, 4 });

        var detail = new EmailDetail
        {
            Id = 42,
            AccountId = 1,
            FolderId = 1,
            Uid = 7,
            Subject = "Fw: 1014473 / MBL+HBL",
            FromName = "Ada",
            FromEmail = "ada@example.com",
            To = { new EmailAddress("me@example.com", "我") },
            Cc = { new EmailAddress("cc@example.com", "抄送") },
            DateUtc = new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero),
            MessageId = "abc@example.com",
            BodyState = BodyState.Downloaded,
            BodyText = "纯文本正文",
            BodyHtml = "<p>HTML 正文</p>",
            Attachments =
            {
                new AttachmentInfo
                {
                    FileName = "报价单.pdf",
                    ContentType = "application/pdf",
                    IsInline = false,
                    LocalPath = attachmentPath
                }
            }
        };

        var path = await MailEmlExporter.ExportAsync(detail, _root);

        Assert.NotNull(path);
        Assert.EndsWith("Fw_ 1014473 _ MBL+HBL.eml", Path.GetFileName(path));   // 非法字符被替换
        Assert.True(File.Exists(path));

        var message = await MimeMessage.LoadAsync(path!);
        Assert.Equal("Fw: 1014473 / MBL+HBL", message.Subject);
        Assert.Equal("ada@example.com", ((MailboxAddress)message.From[0]).Address);
        Assert.Equal("abc@example.com", message.MessageId);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero), message.Date);

        var text = message.TextBody;
        var html = message.HtmlBody;
        Assert.Contains("纯文本正文", text);
        Assert.Contains("HTML 正文", html);

        var attachment = Assert.IsAssignableFrom<MimePart>(Assert.Single(message.Attachments));
        Assert.Equal("报价单.pdf", attachment.FileName);
    }

    [Fact]
    public async Task 导出_正文内嵌图片改成cid并挂成related资源()
    {
        Directory.CreateDirectory(_root);
        var imagePath = Path.Combine(_root, "logo.png");
        await File.WriteAllBytesAsync(imagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var detail = new EmailDetail
        {
            Id = 44,
            Subject = "内嵌图片",
            FromEmail = "ada@example.com",
            DateUtc = DateTimeOffset.UtcNow,
            BodyState = BodyState.Downloaded,
            BodyHtml = $"<p>看这个：<img src=\"file:///{imagePath.Replace('\\', '/')}\"></p>",
            Attachments =
            {
                new AttachmentInfo
                {
                    FileName = "logo.png",
                    ContentType = "image/png",
                    IsInline = true,
                    LocalPath = imagePath
                }
            }
        };

        var path = await MailEmlExporter.ExportAsync(detail, _root);
        var message = await MimeMessage.LoadAsync(path!);

        var related = Assert.IsType<MultipartRelated>(message.Body);
        var image = Assert.Single(related.OfType<MimePart>(), p => p.ContentType.MediaType == "image");
        var contentId = image.ContentId;
        Assert.False(string.IsNullOrEmpty(contentId));

        var html = Assert.Single(related.OfType<TextPart>(), p => p.IsHtml).Text;
        Assert.Contains("cid:" + contentId, html);
        Assert.DoesNotContain("file:///", html);
    }
    [Fact]
    public async Task 导出_正文未下载时返回空()
    {
        var detail = new EmailDetail
        {
            Id = 43,
            Subject = "还没下载",
            BodyState = BodyState.NotDownloaded
        };

        var path = await MailEmlExporter.ExportAsync(detail, _root);

        Assert.Null(path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响结论
        }
    }
}
