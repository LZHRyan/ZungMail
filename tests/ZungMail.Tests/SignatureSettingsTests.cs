using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 按账户签名（设置 → 签名）：单独设置优先、未启用返回空、没有单独设置时回退旧版全局签名。
/// </summary>
[Trait("Category", "Unit")]
public sealed class SignatureSettingsTests
{
    [Fact]
    public void ResolveSignatureHtml_账户单独设置且启用_返回富文本原文()
    {
        var settings = new AppSettings { Signature = "旧的全公司签名" };
        settings.Signatures.Add(new AccountSignature
        {
            AccountId = 7,
            IsEnabled = true,
            Html = "<p><strong>张三</strong>｜货代操作</p>"
        });

        Assert.Equal("<p><strong>张三</strong>｜货代操作</p>", settings.ResolveSignatureHtml(7));
    }

    [Fact]
    public void ResolveSignatureHtml_账户关闭签名_返回空且不落回全局签名()
    {
        var settings = new AppSettings { Signature = "旧的全公司签名" };
        settings.Signatures.Add(new AccountSignature
        {
            AccountId = 7,
            IsEnabled = false,
            Html = "<p>张三</p>"
        });

        Assert.Equal("", settings.ResolveSignatureHtml(7));
    }

    [Fact]
    public void ResolveSignatureHtml_未单独设置账户_回退旧版全局签名并转成HTML()
    {
        var settings = new AppSettings { Signature = "张三\n市场部" };

        Assert.Equal("<p>张三</p><p>市场部</p>", settings.ResolveSignatureHtml(99));
    }

    [Fact]
    public void ResolveSignatureHtml_什么都没配_返回空()
    {
        var settings = new AppSettings();

        Assert.Equal("", settings.ResolveSignatureHtml(1));
    }

    [Fact]
    public void FindSignature_只匹配对应账户()
    {
        var settings = new AppSettings();
        settings.Signatures.Add(new AccountSignature { AccountId = 1, Html = "<p>A</p>" });
        settings.Signatures.Add(new AccountSignature { AccountId = 2, Html = "<p>B</p>" });

        Assert.Equal("<p>B</p>", settings.FindSignature(2)!.Html);
        Assert.Null(settings.FindSignature(3));
    }
}
