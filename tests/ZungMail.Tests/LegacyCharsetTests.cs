using System.Text;
using ZungMail.Mail;
using ZungMail.Mail.DependencyInjection;
using MailKit;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 非 UTF-8 邮件（GB2312/GBK 中文、KOI8-R / Windows-1251 俄语）的解码验证。
///
/// 这些字符集不在 .NET 的默认编码表内，MimeKit 取不到编码时会退回 Latin-1，
/// 于是整封信变成乱码。邮件层必须在被使用前注册代码页提供程序。
/// </summary>
[Trait("Category", "Unit")]
public sealed class LegacyCharsetTests
{
    /// <summary>走与 App 相同的启动路径：注册邮件层服务（其中包含旧式字符集注册）。</summary>
    private static void BootstrapMailLayer() => new ServiceCollection().AddZungMailMail();

    [Fact]
    public void 旧式字符集_GBK主题与KOI8R正文_可正确解码()
    {
        BootstrapMailLayer();

        // 主题与发件人名：=?gb2312?B?...?= = 「回复：附件是修改后的」
        const string encoded = "=?gb2312?B?u9i4tKO6uL28/srH0N64xLrztcQ=?=";

        var raw = string.Join("\r\n", new[]
        {
            $"From: {encoded} <sender@example.com>",
            "To: me@example.com",
            $"Subject: {encoded}",
            "Date: Fri, 16 Oct 2026 10:00:00 +0800",
            "Message-Id: <legacy-gbk@example.com>",
            "MIME-Version: 1.0",
            "Content-Type: text/plain; charset=koi8-r",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            "+sTSwdfT1NfVytTFLCDLz87UwcvU2SDBx8XO1MEgzsnWxQ=="
        });

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var message = MimeMessage.Load(stream);

        Assert.Equal("回复：附件是修改后的", message.Subject);
        Assert.Equal("回复：附件是修改后的", message.From.Mailboxes.First().Name);
        Assert.Equal("Здравствуйте, контакты агента ниже", message.TextBody);
    }

    [Fact]
    public void ImapEnvelope保留RFC2047主题时_MimeMapper仍可正确解码()
    {
        BootstrapMailLayer();
        var detail = MimeMapper.ToDetail(1, 2, 3, new RemoteMessageSummary
        {
            Envelope = new Envelope
            {
                Subject = "=?windows-1251?B?0fe48iDv7iDn4P/i6uU=?="
            }
        });

        Assert.Equal("Счёт по заявке", detail.Subject);
    }

    [Fact]
    public void 旧式字符集_Windows1251主题_可正确解码()
    {
        BootstrapMailLayer();

        var raw = string.Join("\r\n", new[]
        {
            "From: a@example.com",
            "To: me@example.com",
            "Subject: =?windows-1251?B?0fe48iDv7iDn4P/i6uU=?=",
            "Date: Fri, 16 Oct 2026 10:00:00 +0300",
            "Message-Id: <legacy-1251@example.com>",
            "MIME-Version: 1.0",
            "Content-Type: text/plain; charset=utf-8",
            "Content-Transfer-Encoding: 8bit",
            string.Empty,
            "ok"
        });

        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var message = MimeMessage.Load(stream);

        Assert.Equal("Счёт по заявке", message.Subject);
    }
}
