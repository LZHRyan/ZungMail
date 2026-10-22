using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 签名的导出 / 导入：文件带格式标识与版本号，导入按邮箱地址匹配本地账户。
/// 跨机器导入时账户 id 不一样，所以身份只能是邮箱。
/// </summary>
[Trait("Category", "Unit")]
public sealed class SignatureShareServiceTests
{
    private static readonly SignatureShareEntry Sample = new()
    {
        Account = "sales08@examplefreight.com",
        Enabled = true,
        Html = "<p><strong>Ada</strong>｜示例物流</p>"
    };

    [Fact]
    public void 导出的文件能被解析回来()
    {
        var json = SignatureShareService.Serialize(new[] { Sample });

        var parsed = SignatureShareService.Parse(json);

        Assert.True(parsed.Ok);
        var entry = Assert.Single(parsed.Entries);
        Assert.Equal(Sample.Account, entry.Account);
        Assert.True(entry.Enabled);
        Assert.Equal(Sample.Html, entry.Html);
        Assert.Contains("zungmail.signatures", json);
    }

    [Fact]
    public void 空账户的条目不会被导出()
    {
        var json = SignatureShareService.Serialize(new[]
        {
            Sample,
            new SignatureShareEntry { Account = "  ", Html = "<p>x</p>" }
        });

        Assert.True(SignatureShareService.Parse(json).Ok);
        Assert.Single(SignatureShareService.Parse(json).Entries);
    }

    [Fact]
    public void 非签名文件被拒绝()
    {
        var parsed = SignatureShareService.Parse("{\"format\":\"zungmail.templates\",\"version\":1,\"templates\":[]}");

        Assert.False(parsed.Ok);
        Assert.Contains("签名文件", parsed.Message);
    }

    [Fact]
    public void 旧版本导出的签名文件仍可导入()
    {
        // 改名（MailClient → ZungMail）前导出的文件格式标识是 mailclient.signatures
        var json = """
{
  "Format": "mailclient.signatures",
  "Version": 1,
  "App": "MailClient",
  "Signatures": [ { "Account": "sales08@examplefreight.com", "Enabled": true, "Html": "<p>旧签名</p>" } ]
}
""";

        var parsed = SignatureShareService.Parse(json);

        Assert.True(parsed.Ok, parsed.Message);
        var entry = Assert.Single(parsed.Entries);
        Assert.Equal("sales08@examplefreight.com", entry.Account);
        Assert.Equal("<p>旧签名</p>", entry.Html);
    }

    [Fact]
    public void 损坏的JSON被拒绝且给出原因()
    {
        var parsed = SignatureShareService.Parse("{ not json");

        Assert.False(parsed.Ok);
        Assert.Contains("JSON", parsed.Message);
    }

    [Fact]
    public void 更高版本的文件被拒绝()
    {
        // 分享文件沿用模板分享的 PascalCase 字段名
        var json = SignatureShareService.Serialize(new[] { Sample });
        var bumped = json.Replace("\"Version\": 1", "\"Version\": 99");
        Assert.NotEqual(json, bumped);

        var parsed = SignatureShareService.Parse(bumped);

        Assert.False(parsed.Ok);
        Assert.Contains("升级", parsed.Message);
    }

    [Fact]
    public void 空文件与无条目都被拒绝()
    {
        Assert.False(SignatureShareService.Parse("").Ok);
        Assert.False(SignatureShareService.Parse(null).Ok);

        var empty = SignatureShareService.Serialize(Array.Empty<SignatureShareEntry>());
        var parsed = SignatureShareService.Parse(empty);
        Assert.False(parsed.Ok);
        Assert.Contains("没有可导入", parsed.Message);
    }

    [Fact]
    public void 按邮箱匹配本地账户且大小写不敏感()
    {
        var plan = SignatureShareService.Plan(
            new[] { new SignatureShareEntry { Account = "Sales08@ExampleFreight.com", Html = "<p>a</p>" } },
            new[] { (7L, "sales08@examplefreight.com"), (9L, "ops@examplefreight.com") });

        var match = Assert.Single(plan.Matched);
        Assert.Equal(7, match.AccountId);
        Assert.Empty(plan.UnknownAccounts);
        Assert.True(plan.HasAnything);
    }

    [Fact]
    public void 本地没有的邮箱进未知列表()
    {
        var plan = SignatureShareService.Plan(
            new[]
            {
                new SignatureShareEntry { Account = "sales08@examplefreight.com", Html = "<p>a</p>" },
                new SignatureShareEntry { Account = "nobody@example.com", Html = "<p>b</p>" }
            },
            new[] { (7L, "sales08@examplefreight.com") });

        Assert.Single(plan.Matched);
        Assert.Equal("nobody@example.com", Assert.Single(plan.UnknownAccounts));
    }

    [Fact]
    public void 一个邮箱都没命中时HasAnything为假()
    {
        var plan = SignatureShareService.Plan(
            new[] { new SignatureShareEntry { Account = "nobody@example.com", Html = "<p>b</p>" } },
            new[] { (7L, "sales08@examplefreight.com") });

        Assert.False(plan.HasAnything);
        Assert.Single(plan.UnknownAccounts);
    }

    [Fact]
    public void 同一账户多条以最后一条为准()
    {
        var plan = SignatureShareService.Plan(
            new[]
            {
                new SignatureShareEntry { Account = "sales08@examplefreight.com", Html = "<p>old</p>" },
                new SignatureShareEntry { Account = "SALES08@examplefreight.com", Html = "<p>new</p>" }
            },
            new[] { (7L, "sales08@examplefreight.com") });

        var match = Assert.Single(plan.Matched);
        Assert.Equal("<p>new</p>", match.Entry.Html);
    }
}
