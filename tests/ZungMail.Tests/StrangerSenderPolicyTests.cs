using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 陌生发件人判定口径（纯逻辑）：通讯录命中、历史来信命中、自有账户地址都算熟人；
/// 空地址与非法地址不打标，避免脏数据把同步搞坏。
/// </summary>
[Trait("Category", "Unit")]
public sealed class StrangerSenderPolicyTests
{
    private static readonly IReadOnlySet<string> NoKnown = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> NoOwn = new HashSet<string>(StringComparer.Ordinal);

    [Fact]
    public void 首次来信算陌生()
    {
        Assert.True(StrangerSenderPolicy.IsStranger("newagent@example.com", NoKnown, NoOwn));
    }

    [Fact]
    public void 通讯录里有时不算陌生()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "sales@partner.com" };

        Assert.False(StrangerSenderPolicy.IsStranger("sales@partner.com", known, NoOwn));
    }

    [Fact]
    public void 历史来信里有该地址时不算陌生()
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "ops@forwarder.cn" };

        Assert.False(StrangerSenderPolicy.IsStranger("OPS@Forwarder.CN ", known, NoOwn));
    }

    [Fact]
    public void 自有账户地址不算陌生()
    {
        var own = new HashSet<string>(StringComparer.Ordinal) { "me@examplefreight.com" };

        Assert.False(StrangerSenderPolicy.IsStranger("Me@ExampleFreight.com", NoKnown, own));
    }

    [Fact]
    public void 地址归一_去空白并小写()
    {
        Assert.Equal("a@b.com", StrangerSenderPolicy.Normalize("  A@B.CoM  "));

        var set = StrangerSenderPolicy.NormalizeAll(new[] { "  A@B.com ", "", null, "a@b.COM" });
        Assert.Single(set);
        Assert.Contains("a@b.com", set);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("不是邮箱")]
    public void 空地址与非法地址不打标(string? address)
    {
        Assert.False(StrangerSenderPolicy.IsStranger(address, NoKnown, NoOwn));
    }

    [Fact]
    public void 纯函数只看地址本身_自有域名的豁免在判定器里()
    {
        // 纯函数不认识「我的企业域名」；同域的同事由 StrangerSenderJudge 短路成熟人（见同步集成测试）
        var known = new HashSet<string>(StringComparer.Ordinal) { "op01-meds@examplefreight.com" };

        Assert.True(StrangerSenderPolicy.IsStranger("op99-meds@examplefreight.com", known, NoOwn));
    }
}
