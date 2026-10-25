using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Mail;
using MimeKit;
using Xunit;

namespace ZungMail.Tests;

/// <summary>正文提取：multipart/related 内嵌图片必须落盘并改写 cid: 引用。</summary>
[Trait("Category", "Unit")]
public sealed class MimeMapperInlineImageTests
{
    [Fact]
    public async Task 内嵌图片落盘_并把cid改写为本地文件()
    {
        using var paths = new TempPaths();
        var message = new MimeMessage();

        var builder = new BodyBuilder { HtmlBody = "<p>你好</p><img src=\"cid:logo@test\">" };
        var image = new MimePart("image", "png") { ContentId = "logo@test" };
        image.Content = new MimeContent(new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        builder.LinkedResources.Add(image);
        message.Body = builder.ToMessageBody();

        var detail = new EmailDetail { AccountId = 1, Id = 2 };
        var result = await MimeMapper.ExtractBodyAsync(message, paths, detail, CancellationToken.None);

        var inline = Assert.Single(result.Attachments);
        Assert.True(inline.IsInline);
        Assert.EndsWith(".png", inline.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(inline.LocalPath);
        Assert.True(File.Exists(inline.LocalPath));
        Assert.DoesNotContain("cid:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("file:///", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 正文里 base64 内嵌的截图（data:image/png;base64,…）要落盘并改成 file:///：
    /// 8.3MB 的水单邮件就是这种写法（正文 6.77MB、57 张图），留在库里既占空间又拖慢渲染。
    /// </summary>
    [Fact]
    public async Task 正文里的base64内嵌图落盘_并改写为本地文件()
    {
        using var paths = new TempPaths();
        var message = new MimeMessage();

        var png = new byte[2048];
        for (var i = 0; i < png.Length; i++) png[i] = (byte)(i % 251);
        var base64 = Convert.ToBase64String(png);

        var builder = new BodyBuilder
        {
            HtmlBody = $"<p>尊敬的客户：</p><img src=\"data:image/png;base64,{base64}\"><p>提单见附件</p>"
        };
        message.Body = builder.ToMessageBody();

        var detail = new EmailDetail { AccountId = 1, Id = 4 };
        var result = await MimeMapper.ExtractBodyAsync(message, paths, detail, CancellationToken.None);

        // 正文里不再有 base64，只剩本地文件引用，正文长度塌缩
        Assert.DoesNotContain("base64", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("file:///", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Html.Length < 400, $"正文应只剩引用，实际 {result.Html.Length} 字符");
        Assert.Contains("提单见附件", result.Html);

        // 落盘的文件是内嵌附件，导出 .eml / 转发内联都靠它
        var inline = Assert.Single(result.Attachments);
        Assert.True(inline.IsInline);
        Assert.Equal("image/png", inline.ContentType);
        Assert.Equal(png.Length, inline.Size);
        Assert.NotNull(inline.LocalPath);
        Assert.True(File.Exists(inline.LocalPath));
        Assert.Equal(png, await File.ReadAllBytesAsync(inline.LocalPath!));
    }

    [Fact]
    public async Task 普通附件仍按文件名保存()
    {
        using var paths = new TempPaths();
        var message = new MimeMessage();

        var builder = new BodyBuilder { HtmlBody = "<p>见附件</p>" };
        builder.Attachments.Add("对账单.pdf", new MemoryStream(new byte[] { 1, 2, 3 }));
        message.Body = builder.ToMessageBody();

        var detail = new EmailDetail { AccountId = 1, Id = 3 };
        var result = await MimeMapper.ExtractBodyAsync(message, paths, detail, CancellationToken.None);

        var attachment = Assert.Single(result.Attachments);
        Assert.False(attachment.IsInline);
        Assert.Equal("对账单.pdf", attachment.FileName);
        Assert.True(File.Exists(attachment.LocalPath));
        Assert.DoesNotContain("cid:", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 仅收取正文_附件不落盘但保留条目()
    {
        using var paths = new TempPaths();
        var message = new MimeMessage();

        var builder = new BodyBuilder { HtmlBody = "<p>你好</p><img src=\"cid:logo@test\">" };
        var image = new MimePart("image", "png") { ContentId = "logo@test" };
        image.Content = new MimeContent(new MemoryStream(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        builder.LinkedResources.Add(image);
        builder.Attachments.Add("提单.pdf", new MemoryStream(new byte[] { 1, 2, 3, 4 }));
        message.Body = builder.ToMessageBody();

        var detail = new EmailDetail { AccountId = 1, Id = 4 };
        var result = await MimeMapper.ExtractBodyAsync(
            message, paths, detail, CancellationToken.None, saveAttachments: false);

        // 普通附件：条目还在（列表里仍能看到名字与大小），但不落盘、不算已下载
        var attachment = Assert.Single(result.Attachments, a => !a.IsInline);
        Assert.Equal("提单.pdf", attachment.FileName);
        Assert.Null(attachment.LocalPath);
        Assert.False(attachment.IsDownloaded);

        // 内嵌图片属于正文，这一档仍要落盘，否则正文会全是破图
        var inline = Assert.Single(result.Attachments, a => a.IsInline);
        Assert.NotNull(inline.LocalPath);
        Assert.True(File.Exists(inline.LocalPath));
        Assert.DoesNotContain("cid:", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TempPaths : IAppPaths, IDisposable
    {
        public TempPaths()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "zungmail-tests", Guid.NewGuid().ToString("N"));
            EnsureCreated();
        }

        public string RootDirectory { get; }
        public string DatabasePath => Path.Combine(RootDirectory, "mail.db");
        public string TemplatesPath => Path.Combine(RootDirectory, "templates.json");
        public string SettingsPath => Path.Combine(RootDirectory, "settings.json");
        public string LogDirectory => Path.Combine(RootDirectory, "logs");
        public string EmlDirectory => Path.Combine(RootDirectory, "eml");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(EmlDirectory);
        }

        public void Dispose()
        {
            try { Directory.Delete(RootDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
