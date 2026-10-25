using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 正文双击图片的桥接：脚本回传的消息要能被宿主正确解出来，
/// 另存为要能从字节猜出扩展名。两边任何一处漂移，双击都会变成没反应。
/// </summary>
[Trait("Category", "Unit")]
public sealed class MailBodyImageMessageTests
{
    [Fact]
    public void 解析脚本回传的完整消息()
    {
        var json = """
            {"type":"mail-image-dblclick","src":"data:image/png;base64,AAAA","alt":"运价表","width":640,"height":480}
            """;

        var message = MailBodyImageMessage.TryParse(json);

        Assert.NotNull(message);
        Assert.Equal("data:image/png;base64,AAAA", message!.Source);
        Assert.Equal("运价表", message.AltText);
        Assert.Equal(640, message.NaturalWidth);
        Assert.Equal(480, message.NaturalHeight);
    }

    [Fact]
    public void 缺少可选字段时给默认值()
    {
        var message = MailBodyImageMessage.TryParse("""{"type":"mail-image-dblclick","src":"https://demo.example/a.png"}""");

        Assert.NotNull(message);
        Assert.Equal(string.Empty, message!.AltText);
        Assert.Equal(0, message.NaturalWidth);
        Assert.Equal(0, message.NaturalHeight);
    }

    [Fact]
    public void 尺寸用字符串也能读出来()
    {
        var message = MailBodyImageMessage.TryParse(
            """{"type":"mail-image-dblclick","src":"https://demo.example/a.png","width":"128","height":"96"}""");

        Assert.NotNull(message);
        Assert.Equal(128, message!.NaturalWidth);
        Assert.Equal(96, message.NaturalHeight);
    }

    [Theory]
    [InlineData("""{"type":"other","src":"https://demo.example/a.png"}""")]   // 类型不对
    [InlineData("""{"type":"mail-image-dblclick"}""")]                       // 没有地址
    [InlineData("""{"type":"mail-image-dblclick","src":"  "}""")]            // 地址是空白
    [InlineData("""{"src":"https://demo.example/a.png"}""")]                 // 没有类型
    [InlineData("not json at all")]                                          // 不是 JSON
    [InlineData("")]
    [InlineData(null)]
    public void 非法消息一律返回空(string? json)
    {
        Assert.Null(MailBodyImageMessage.TryParse(json));
    }

    [Fact]
    public void 消息类型常量与文档约定一致()
    {
        // 脚本里的 type 字面量由这个常量拼出来（见 ZungMail.App.Services.MailBodyImageScript），
        // UiTests 里另有一条断言守住「脚本确实用了这个常量」
        Assert.Equal("mail-image-dblclick", MailBodyImageMessage.TypeName);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, ".gif")]
    [InlineData(new byte[] { 0x42, 0x4D }, ".bmp")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 }, ".webp")]
    [InlineData(new byte[] { 0x00, 0x01 }, ".png")]
    public void 按文件头猜扩展名(byte[] bytes, string expected)
    {
        Assert.Equal(expected, ImageFormatSniffer.GuessExtension(bytes));
    }
}
