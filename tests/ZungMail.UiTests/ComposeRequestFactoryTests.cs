using System;
using System.IO;
using ZungMail.App.Services;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 回复 / 回复全部 / 转发的撰写请求装配：主窗口与独立阅读窗口共用这一份实现，
/// 这里守住收件人推导、主题前缀、回复线索与引用块，避免两个入口各写一套后漂移。
/// </summary>
[Trait("Category", "Unit")]
public sealed class ComposeRequestFactoryTests
{
    private static EmailDetail CreateDetail() => new()
    {
        Id = 7,
        AccountId = 3,
        MessageId = "<m7@forwarder.cn>",
        Subject = "MBL+HBL DEMO-REF-0001",
        FromName = "Sofya Petrova",
        FromEmail = "spam2019@example.com",
        BodyHtml = "<p>格式化的<b>原文</b></p>",
        BodyText = "格式化的原文",
        To =
        {
            new EmailAddress("me@example.com", "我"),
            new EmailAddress("ops@examplefreight.com", "Ops")
        },
        Cc = { new EmailAddress("boss@examplefreight.com", "Boss") },
        References = { "<m1@forwarder.cn>", "<m2@forwarder.cn>" }
    };

    [Fact]
    public void 回复_发给发件人并带上回复线索与引用块()
    {
        var request = ComposeRequestFactory.ForReply(CreateDetail(), ComposeMode.Reply, "me@example.com");

        Assert.NotNull(request);
        Assert.Equal(ComposeMode.Reply, request!.Mode);
        Assert.Equal(3, request.AccountId);
        Assert.Equal("Sofya Petrova <spam2019@example.com>", request.To);
        Assert.Equal(string.Empty, request.Cc);
        Assert.Equal("Re: MBL+HBL DEMO-REF-0001", request.Subject);
        Assert.Equal("<m7@forwarder.cn>", request.InReplyTo);
        Assert.Contains("<m2@forwarder.cn>", request.References);

        // 正文是 HTML 引用块：先空行给用户写，再是原始邮件抬头 + 原文
        Assert.StartsWith("<p><br/></p>", request.Body);
        Assert.Contains("mc-quote", request.Body);
        Assert.Contains("原始邮件", request.Body);
        Assert.Contains("格式化的<b>原文</b>", request.Body);
    }

    [Fact]
    public void 回复全部_抄送里排除自己与发件人()
    {
        var request = ComposeRequestFactory.ForReply(CreateDetail(), ComposeMode.ReplyAll, "me@example.com");

        Assert.NotNull(request);
        // 原收件人里的「我」不再出现；Ops 与 Boss 保留（原样保留过一次）
        Assert.DoesNotContain("我 <me@example.com>", request!.Cc);
        Assert.Contains("Ops", request.Cc);
        Assert.Contains("Boss", request.Cc);
    }

    [Fact]
    public void 转发_不带回复线索且主题加Fwd前缀()
    {
        var request = ComposeRequestFactory.ForReply(CreateDetail(), ComposeMode.Forward, "me@example.com");

        Assert.NotNull(request);
        Assert.Equal(ComposeMode.Forward, request!.Mode);
        Assert.Equal(string.Empty, request.To);
        Assert.Equal("Fwd: MBL+HBL DEMO-REF-0001", request.Subject);
        Assert.Null(request.InReplyTo);
        Assert.Null(request.References);
        Assert.Contains("mc-quote", request.Body);
    }

    [Fact]
    public void 已有Re前缀时不再重复加()
    {
        var detail = CreateDetail();
        detail.Subject = "Re: 1014473 MBL";

        var request = ComposeRequestFactory.ForReply(detail, ComposeMode.Reply, "me@example.com");

        Assert.Equal("Re: 1014473 MBL", request!.Subject);
    }

    [Fact]
    public void 草稿邮件不能回复转发()
    {
        var detail = CreateDetail();
        detail.Flags = MessageFlags.Draft;

        Assert.Null(ComposeRequestFactory.ForReply(detail, ComposeMode.Reply, "me@example.com"));
        Assert.Null(ComposeRequestFactory.ForReply(detail, ComposeMode.Forward, "me@example.com"));
    }

    [Fact]
    public void 内嵌图片按附件目录白名单读取()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-quote-img-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var inside = Path.Combine(root, "logo.png");
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");
        File.WriteAllBytes(inside, png);

        try
        {
            Assert.Equal(png, ComposeRequestFactory.ReadLocalImage(inside, root));
            // 目录外的一律拒绝
            Assert.Null(ComposeRequestFactory.ReadLocalImage(Path.Combine(Path.GetTempPath(), "other.png"), root));
            Assert.Null(ComposeRequestFactory.ReadLocalImage(inside, string.Empty));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { /* 清理失败不影响结论 */ }
        }
    }
}
