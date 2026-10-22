using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.Tests;

/// <summary>客户端锁定口令的派生与校验。</summary>
[Trait("Category", "Unit")]
public class ClientLockSecretTests
{
    [Fact]
    public void 生成后可用同一口令校验()
    {
        var secret = ClientLockSecret.Create("freight-2026");

        Assert.StartsWith("pbkdf2-sha256$", secret);
        Assert.True(ClientLockSecret.Verify(secret, "freight-2026"));
        Assert.False(ClientLockSecret.Verify(secret, "freight-2027"));
        Assert.False(ClientLockSecret.Verify(secret, "Freight-2026"));
    }

    [Fact]
    public void 每次生成使用不同随机盐()
    {
        var first = ClientLockSecret.Create("same-password");
        var second = ClientLockSecret.Create("same-password");

        Assert.NotEqual(first, second);
        Assert.True(ClientLockSecret.Verify(first, "same-password"));
        Assert.True(ClientLockSecret.Verify(second, "same-password"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plain-text")]
    [InlineData("pbkdf2-sha256$120000$notbase64$hash")]
    [InlineData("pbkdf2-sha256$10$AAAA$AAAA")]
    [InlineData("pbkdf2-sha512$120000$AAAA$AAAA")]
    public void 非法校验串一律不通过(string? secret)
        => Assert.False(ClientLockSecret.Verify(secret, "any-password"));

    [Fact]
    public void 空口令不通过()
    {
        var secret = ClientLockSecret.Create("good-password");

        Assert.False(ClientLockSecret.Verify(secret, null));
        Assert.False(ClientLockSecret.Verify(secret, ""));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("abc", false)]
    [InlineData("abcd", true)]
    [InlineData("长密码也要支持", true)]
    public void 口令长度下限为四位(string password, bool expected)
        => Assert.Equal(expected, ClientLockSecret.IsAcceptablePassword(password));
}
