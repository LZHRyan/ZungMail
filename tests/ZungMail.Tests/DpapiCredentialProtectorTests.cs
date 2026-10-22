using System.Security.Cryptography;
using ZungMail.Infrastructure.Security;
using ZungMail.Infrastructure.Storage;
using Xunit;

namespace ZungMail.Tests;

/// <summary>凭据加密（NFR-S2）：DPAPI 往返、版本前缀与历史 AES 密文兼容。</summary>
[Trait("Category", "Integration")]
public sealed class DpapiCredentialProtectorTests : IDisposable
{
    private readonly string _root;
    private readonly DpapiCredentialProtector _protector;

    public DpapiCredentialProtectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ZungMailTests", Guid.NewGuid().ToString("N"));
        _protector = new DpapiCredentialProtector(new AppPaths(_root));
    }

    [Theory]
    [InlineData("plain-password-123")]
    [InlineData("授权码&特殊字符<>\"'")]
    public void Protect_Unprotect_往返一致(string plain)
    {
        var cipher = _protector.Protect(plain);

        Assert.StartsWith(DpapiCredentialProtector.Prefix, cipher);
        Assert.DoesNotContain(plain, cipher);
        Assert.Equal(plain, _protector.Unprotect(cipher));
    }

    [Fact]
    public void Protect_相同明文产生不同密文()
    {
        var first = _protector.Protect("same-secret");
        var second = _protector.Protect("same-secret");

        Assert.NotEqual(first, second); // DPAPI 每次加密使用随机会话密钥
    }

    [Fact]
    public void Protect_新实例可解密既有凭据()
    {
        var cipher = _protector.Protect("persisted-secret");

        var reopened = new DpapiCredentialProtector(new AppPaths(_root));

        Assert.Equal("persisted-secret", reopened.Unprotect(cipher));
    }

    [Fact]
    public void Unprotect_历史AES密文仍可解密()
    {
        var legacyCipher = new AesCredentialProtector(new AppPaths(_root)).Protect("legacy-secret");
        Assert.False(legacyCipher.StartsWith(DpapiCredentialProtector.Prefix, StringComparison.Ordinal));

        Assert.Equal("legacy-secret", _protector.Unprotect(legacyCipher));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Unprotect_改名前的DPAPI密文仍可解密()
    {
        Assert.Equal("old-brand-secret", _protector.Unprotect(CreateLegacyEntropyCipher("old-brand-secret")));
    }

    /// <summary>
    /// 用改名前的附加熵（MailClient.Credentials.v1）手工造一份旧密文：
    /// 产品改名后旧熵必须保持可解密，否则升级后所有账户都要重填授权码。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string CreateLegacyEntropyCipher(string plain)
    {
        var legacyEntropy = System.Text.Encoding.UTF8.GetBytes("MailClient.Credentials.v1");
        var cipher = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(plain),
            legacyEntropy,
            DataProtectionScope.CurrentUser);

        return DpapiCredentialProtector.Prefix + Convert.ToBase64String(cipher);
    }

    [Fact]
    public void Unprotect_DPAPI密文被篡改时抛出()
    {
        var cipher = _protector.Protect("secret");
        var raw = Convert.FromBase64String(cipher[DpapiCredentialProtector.Prefix.Length..]);
        raw[^1] ^= 0xFF;
        var tampered = DpapiCredentialProtector.Prefix + Convert.ToBase64String(raw);

        Assert.ThrowsAny<CryptographicException>(() => { _protector.Unprotect(tampered); });
    }

    [Theory]
    [InlineData("dpapi:1:不是 Base64 的字符串!!!")]
    [InlineData("dpapi:1:")]
    public void Unprotect_非法DPAPI密文抛出(string invalid)
        => Assert.Throws<CryptographicException>(() => { _protector.Unprotect(invalid); });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
