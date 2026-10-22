using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 仿冒域名判定（纯函数）：四类命中、六类不命中、容差差异与公共邮箱排除。
/// 判定在渲染时计算，所以这些用例同时约束列表与阅读区的行为。
/// </summary>
[Trait("Category", "Unit")]
public sealed class PhishingDomainPolicyTests
{
    private static readonly IReadOnlySet<string> Own =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "examplefreight.com" };

    private static readonly IReadOnlySet<string> NoOwn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void 形近域名命中且说明文案带基准域名()
    {
        var verdict = PhishingDomainPolicy.Evaluate("sales08@examplefreightt.com", Own);

        Assert.NotNull(verdict);
        Assert.Equal(PhishingDomainReason.Lookalike, verdict!.Reason);
        Assert.Equal("examplefreight.com", verdict.OwnDomain);
        Assert.Equal("examplefreightt.com", verdict.SenderDomain);
        Assert.Contains("examplefreight.com", verdict.Summary);
    }

    [Fact]
    public void 相邻字符换位也算形近()
    {
        // exmaplefreight ↔ examplefreight：只有 a/h 位置互换
        Assert.NotNull(PhishingDomainPolicy.Evaluate("ops@exmaplefreight.com", Own));
    }

    [Theory]
    [InlineData("finance@examp1efreight.com")] // 1 → l
    [InlineData("finance@3xamplefreight.com")] // 0 → o
    public void 混淆字符命中(string address)
    {
        var verdict = PhishingDomainPolicy.Evaluate(address, Own);

        Assert.NotNull(verdict);
        Assert.Equal(PhishingDomainReason.Confusable, verdict!.Reason);
    }

    [Theory]
    [InlineData("ops@examplefreight.cn")]
    [InlineData("ops@examplefreight.net")]
    [InlineData("ops@examplefreight.com.cn")]
    public void 后缀替换命中(string address)
    {
        var verdict = PhishingDomainPolicy.Evaluate(address, Own);

        Assert.NotNull(verdict);
        Assert.Equal(PhishingDomainReason.SuffixSwap, verdict!.Reason);
    }

    [Fact]
    public void 子域伪装命中()
    {
        var verdict = PhishingDomainPolicy.Evaluate("service@examplefreight.com.secure-mail.top", Own);

        Assert.NotNull(verdict);
        Assert.Equal(PhishingDomainReason.SubdomainTrick, verdict!.Reason);
    }

    [Fact]
    public void 同域同事不命中()
    {
        Assert.Null(PhishingDomainPolicy.Evaluate("colleague@examplefreight.com", Own));
    }

    [Fact]
    public void 自有域名的正常子域不命中()
    {
        Assert.Null(PhishingDomainPolicy.Evaluate("notice@mail.examplefreight.com", Own));
    }

    [Fact]
    public void 公共邮箱不作比较对象()
    {
        Assert.Null(PhishingDomainPolicy.Evaluate("friend@qq.com", Own));
        Assert.Null(PhishingDomainPolicy.Evaluate("someone@gmail.com", Own));
    }

    [Fact]
    public void 自有账户是公共邮箱时不产生任何提示()
    {
        // 本机只有 QQ 邮箱：qqq.com 这种正常来信不能被标红
        var ownDomains = PhishingDomainPolicy.OwnDomains(new[] { "someone@qq.com" });

        Assert.Empty(ownDomains);
        Assert.Null(PhishingDomainPolicy.Evaluate("friend@qqq.com", ownDomains));
    }

    [Fact]
    public void 自有域名从账户邮箱里提取且去重()
    {
        var ownDomains = PhishingDomainPolicy.OwnDomains(new[]
        {
            "sales08-meds@ExampleFreight.com",
            "ops@examplefreight.com",
            "someone@qq.com",
            "broken",
            null
        });

        Assert.Single(ownDomains);
        Assert.Contains("examplefreight.com", ownDomains);
    }

    [Fact]
    public void 地址异常不命中()
    {
        Assert.Null(PhishingDomainPolicy.Evaluate("", Own));
        Assert.Null(PhishingDomainPolicy.Evaluate(null, Own));
        Assert.Null(PhishingDomainPolicy.Evaluate("no-at-sign", Own));
        Assert.Null(PhishingDomainPolicy.Evaluate("user@", Own));
        Assert.Null(PhishingDomainPolicy.Evaluate("sales08@examplefreightt.com", NoOwn));
    }

    [Fact]
    public void 容差决定是否命中差两个字符的域名()
    {
        // exmaplefreightt.com 与 examplefreight.com 差 2（插入 i、删除一个 g）
        Assert.Null(PhishingDomainPolicy.Evaluate("ops@exmaplefreightt.com", Own, tolerance: 1));
        Assert.NotNull(PhishingDomainPolicy.Evaluate("ops@exmaplefreightt.com", Own, tolerance: 2));
    }

    [Fact]
    public void 容差夹取在1到2()
    {
        Assert.Equal(1, PhishingDomainPolicy.NormalizeTolerance(0));
        Assert.Equal(1, PhishingDomainPolicy.NormalizeTolerance(-3));
        Assert.Equal(2, PhishingDomainPolicy.NormalizeTolerance(9));
    }

    [Fact]
    public void 顶级后缀解析覆盖两级后缀()
    {
        // 自有域名 examplefreight.com.cn 的主域也是 examplefreight，换后缀同样命中
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "examplefreight.com.cn" };
        var verdict = PhishingDomainPolicy.Evaluate("ops@examplefreight.com", own);

        Assert.NotNull(verdict);
        Assert.Equal(PhishingDomainReason.SuffixSwap, verdict!.Reason);
    }

    [Fact]
    public void 域名提取归一()
    {
        Assert.Equal("sub.example.com", PhishingDomainPolicy.DomainOf("User@Sub.Example.COM"));
        Assert.Equal("example.com", PhishingDomainPolicy.DomainOf("user@example.com."));
        Assert.Equal("", PhishingDomainPolicy.DomainOf("user@"));
        Assert.Equal("", PhishingDomainPolicy.DomainOf("no-at-sign"));
    }

    [Fact]
    public void 距离计算支持换位与插入()
    {
        // 行为通过 Evaluate 覆盖：换位（exmaplefreight↔examplefreight）与插入（examplefreightt）都在容差用例里
        Assert.Equal(PhishingDomainReason.Lookalike, PhishingDomainPolicy.Evaluate("ops@exmaplefreight.com", Own)!.Reason);
        Assert.Equal(PhishingDomainReason.Lookalike, PhishingDomainPolicy.Evaluate("ops@examplefreightt.com", Own)!.Reason);
        Assert.Null(PhishingDomainPolicy.Evaluate("ops@examplefreight.com", Own));
    }
}
