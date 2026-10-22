using System.Security.Cryptography;
using ZungMail.Infrastructure.Security;
using ZungMail.Infrastructure.Storage;
using Xunit;

namespace ZungMail.Tests;

/// <summary>凭据加密（NFR-S2）：AES-256-GCM 往返与篡改检测。</summary>
[Trait("Category", "Integration")]
public sealed class CredentialProtectorTests : IDisposable
{
    private readonly string _root;
    private readonly AesCredentialProtector _protector;

    public CredentialProtectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ZungMailTests", Guid.NewGuid().ToString("N"));
        _protector = new AesCredentialProtector(new AppPaths(_root));
    }

    [Theory]
    [InlineData("plain-password-123")]
    [InlineData("授权码&特殊字符<>\"'")]
    public void Protect_Unprotect_往返一致(string plain)
    {
        var cipher = _protector.Protect(plain);

        Assert.NotEqual(plain, cipher);
        Assert.DoesNotContain(plain, cipher);
        Assert.Equal(plain, _protector.Unprotect(cipher));
    }

    [Fact]
    public void Protect_相同明文产生不同密文()
    {
        var first = _protector.Protect("same-secret");
        var second = _protector.Protect("same-secret");

        Assert.NotEqual(first, second); // 随机 nonce
    }

    [Fact]
    public void Protect_新实例可解密既有凭据()
    {
        var cipher = _protector.Protect("persisted-secret");

        var reopened = new AesCredentialProtector(new AppPaths(_root));

        Assert.Equal("persisted-secret", reopened.Unprotect(cipher));
    }

    [Fact]
    public void Unprotect_密文被篡改时抛出()
    {
        var buffer = Convert.FromBase64String(_protector.Protect("secret"));
        buffer[^1] ^= 0xFF;
        var tampered = Convert.ToBase64String(buffer);

        // 认证标签校验失败时 .NET 抛出 AuthenticationTagMismatchException（CryptographicException 子类）
        Assert.ThrowsAny<CryptographicException>(() => { _protector.Unprotect(tampered); });
    }

    [Theory]
    [InlineData("不是 Base64 的字符串!!!")]
    [InlineData("QUJD")]
    public void Unprotect_非法密文抛出(string invalid)
        => Assert.Throws<CryptographicException>(() => { _protector.Unprotect(invalid); });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
