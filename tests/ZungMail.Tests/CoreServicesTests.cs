using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>Core 层纯逻辑服务的单元测试（FR-02 / FR-05 / FR-06 / FR-10）。</summary>
[Trait("Category", "Unit")]
public sealed class CoreServicesTests
{
    // ---------------- 会话归并（FR-05） ----------------

    [Theory]
    [InlineData("Re: 项目进度", "项目进度")]
    [InlineData("回复：项目进度", "项目进度")]
    [InlineData("回复: RE: 项目进度", "项目进度")]
    [InlineData("Fwd: 报价单", "报价单")]
    [InlineData("转发: FW: RE: 报价单", "报价单")]
    [InlineData("Re[2]: 会议纪要", "会议纪要")]
    [InlineData("刚才的讨论", "刚才的讨论")]
    public void StripPrefixes_剥离中英文回复前缀(string input, string expected)
        => Assert.Equal(expected, SubjectNormalizer.StripPrefixes(input));

    [Fact]
    public void Normalize_小写化并剥离前缀()
        => Assert.Equal("hello world", SubjectNormalizer.Normalize("  RE: Hello World "));

    [Fact]
    public void ThreadKeyBuilder_优先使用References根节点()
    {
        var key = ThreadKeyBuilder.Build(
            "<mid@example.com>",
            new[] { "<root@example.com>", "<mid@example.com>" },
            "Re: 主题");

        Assert.Equal("root@example.com", key);
    }

    [Fact]
    public void ThreadKeyBuilder_无References时退回MessageId()
        => Assert.Equal("mid@example.com", ThreadKeyBuilder.Build("<MID@Example.com>", null, "主题"));

    [Fact]
    public void ThreadKeyBuilder_都缺失时退回主题归一化()
        => Assert.Equal("s:hello", ThreadKeyBuilder.Build(null, null, "Re: Hello"));

    [Fact]
    public void ThreadKeyBuilder_全部缺失时返回空串()
        => Assert.Equal(string.Empty, ThreadKeyBuilder.Build("", Array.Empty<string>(), "   "));

    // ---------------- 搜索分词（FR-06） ----------------

    [Fact]
    public void Tokenize_中文逐字_英文整词()
        => Assert.Equal(new[] { "你", "好", "world", "123" }, FtsTextNormalizer.Tokenize("你好 world 123"));

    [Fact]
    public void Normalize_中文与标点处理()
        => Assert.Equal("hello 世 界", FtsTextNormalizer.Normalize("Hello，世界"));

    [Fact]
    public void BuildMatchExpression_生成前缀与AND组合()
        => Assert.Equal("hello* AND 世* AND 界*", FtsTextNormalizer.BuildMatchExpression("hello 世界"));

    [Fact]
    public void BuildMatchExpression_空查询返回空串()
        => Assert.Equal(string.Empty, FtsTextNormalizer.BuildMatchExpression("   "));

    [Fact]
    public void BuildMatchExpression_去重且限制token数量()
    {
        var expression = FtsTextNormalizer.BuildMatchExpression("abc abc def");
        Assert.Equal("abc* AND def*", expression);
    }

    [Fact]
    public void ToLikePattern_回退模式包裹通配符()
    {
        Assert.Equal("%abc%", FtsTextNormalizer.ToLikePattern(" abc "));
        Assert.Equal("%%", FtsTextNormalizer.ToLikePattern(null));
    }

    [Fact]
    public void IsCjk_区分中日韩与拉丁字符()
    {
        Assert.True(FtsTextNormalizer.IsCjk('中'));
        Assert.True(FtsTextNormalizer.IsCjk('あ'));
        Assert.True(FtsTextNormalizer.IsCjk('한'));
        Assert.False(FtsTextNormalizer.IsCjk('a'));
        Assert.False(FtsTextNormalizer.IsCjk('1'));
    }

    // ---------------- 地址解析（FR-10） ----------------

    [Fact]
    public void Parse_取出显示名与地址()
    {
        var address = MailAddressParser.Parse("张三 <zhangsan@example.com>");

        Assert.Equal("张三", address.Name);
        Assert.Equal("zhangsan@example.com", address.Address);
        Assert.Equal("张三 <zhangsan@example.com>", address.Display);
    }

    [Fact]
    public void ParseList_忽略引号内逗号()
    {
        var list = MailAddressParser.ParseList("\"Doe, John\" <j@x.com>, b@y.com");

        Assert.Equal(2, list.Count);
        Assert.Equal("Doe, John", list[0].Name);
        Assert.Equal("j@x.com", list[0].Address);
        Assert.Equal("b@y.com", list[1].Address);
    }

    [Fact]
    public void ParseList_空输入返回空集合()
    {
        Assert.Empty(MailAddressParser.ParseList(""));
        Assert.Empty(MailAddressParser.ParseList((IEnumerable<string>?)null));
    }

    // ---------------- 服务器预置匹配（FR-02） ----------------

    [Fact]
    public void Resolve_命中内置服务商()
    {
        var preset = new ServerConfigResolver().Resolve("user@263.net");

        Assert.NotNull(preset);
        Assert.Equal("imap.263.net", preset!.ImapHost);
        Assert.Equal(993, preset.ImapPort);
        Assert.Equal(465, preset.SmtpPort);
        Assert.Equal(SecureMode.Ssl, preset.SmtpSecure);
    }

    [Fact]
    public void Resolve_支持企业邮箱子域回退()
    {
        var preset = new ServerConfigResolver().Resolve("user@mail.qq.com");

        Assert.NotNull(preset);
        Assert.Equal("imap.qq.com", preset!.ImapHost);
    }

    [Fact]
    public void Resolve_未收录域名返回null()
        => Assert.Null(new ServerConfigResolver().Resolve("user@example.invalid"));
}
