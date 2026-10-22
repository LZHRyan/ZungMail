using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.Tests;

/// <summary>发件人黑白名单的归一化与匹配规则。</summary>
[Trait("Category", "Unit")]
public class SenderFilterMatchTests
{
    [Theory]
    [InlineData(" boss@Example.com ", "boss@example.com")]
    [InlineData("<boss@example.com>", "boss@example.com")]
    [InlineData("mailto:Boss@Example.com", "boss@example.com")]
    [InlineData("@Example.COM", "@example.com")]
    [InlineData("example.com", "@example.com")]
    public void 归一化_接受常见输入形式(string raw, string expected)
        => Assert.Equal(expected, SenderFilterMatch.Normalize(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("a@b")]                 // 域名没有点
    [InlineData("a@@b.com")]
    [InlineData("a b@c.com")]
    [InlineData("a@b.com,c@d.com")]
    [InlineData("@.com")]
    [InlineData("a@b..com")]
    public void 归一化_拒绝非法输入(string raw)
        => Assert.Null(SenderFilterMatch.Normalize(raw));

    [Theory]
    [InlineData("boss@example.com", "boss@example.com", true)]
    [InlineData("boss@example.com", "Boss@Example.com", true)]
    [InlineData("boss@example.com", "other@example.com", false)]
    [InlineData("@example.com", "anyone@example.com", true)]
    [InlineData("@example.com", "anyone@sub.example.com", false)]
    [InlineData("@example.com", "anyone@other.com", false)]
    [InlineData("boss@example.com", "王总 <boss@example.com>", true)]
    [InlineData("@example.com", "", false)]
    public void 匹配_区分精确地址与整域(string pattern, string address, bool expected)
        => Assert.Equal(expected, SenderFilterMatch.Matches(pattern, address));

    [Fact]
    public void 判定_黑名单命中()
    {
        var filters = new[]
        {
            new SenderFilter { Pattern = "@spam.example", IsBlacklist = true },
            new SenderFilter { Pattern = "boss@ship.example", IsBlacklist = false }
        };

        Assert.Equal(SenderVerdict.Blacklisted, SenderFilterMatch.Classify(filters, "ad@spam.example"));
        Assert.Equal(SenderVerdict.None, SenderFilterMatch.Classify(filters, "ops@ship.example"));
    }

    [Fact]
    public void 判定_白名单优先于黑名单()
    {
        var filters = new[]
        {
            new SenderFilter { Pattern = "@example.com", IsBlacklist = true },
            new SenderFilter { Pattern = "boss@example.com", IsBlacklist = false }
        };

        Assert.Equal(SenderVerdict.Whitelisted, SenderFilterMatch.Classify(filters, "boss@example.com"));
        Assert.Equal(SenderVerdict.Blacklisted, SenderFilterMatch.Classify(filters, "other@example.com"));
    }

    [Fact]
    public void 判定_空名单与空地址不命中()
    {
        Assert.Equal(SenderVerdict.None, SenderFilterMatch.Classify(Array.Empty<SenderFilter>(), "a@b.com"));
        Assert.Equal(SenderVerdict.None, SenderFilterMatch.Classify(null, "a@b.com"));
        Assert.Equal(SenderVerdict.None, SenderFilterMatch.Classify(
            new[] { new SenderFilter { Pattern = "@b.com", IsBlacklist = true } },
            null));
    }

    [Fact]
    public void 显示_包含名单类型与备注()
    {
        var black = new SenderFilter { Pattern = "@spam.example", IsBlacklist = true, Note = "推广" };
        var white = new SenderFilter { Pattern = "@ship.example", IsBlacklist = false };

        Assert.Equal("黑名单 · 推广", black.DetailDisplay);
        Assert.Equal("白名单", white.DetailDisplay);
    }
}
