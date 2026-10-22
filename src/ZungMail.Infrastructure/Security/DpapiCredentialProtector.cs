using System.Security.Cryptography;
using System.Text;
using ZungMail.Core.Abstractions;

namespace ZungMail.Infrastructure.Security;

/// <summary>
/// Windows DPAPI 凭据保护（NFR-S2）：密钥由操作系统按当前用户派生，应用不保管任何密钥材料。
/// <para>
/// 密文格式：<c>dpapi:1:Base64(DPAPI(UTF8, DataProtectionScope.CurrentUser))</c>。
/// 前缀同时充当版本标记；无前缀的历史 AES-GCM 密文交由 <see cref="AesCredentialProtector"/> 解密，
/// 解密成功后账户下次保存会自动改写为 DPAPI 格式，实现平滑迁移。
/// </para>
/// </summary>
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    /// <summary>DPAPI 密文前缀，同时用于识别密文版本。</summary>
    public const string Prefix = "dpapi:1:";

    /// <summary>附加熵：把密文用途绑定到本应用，避免被同用户下其它程序直接复用。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ZungMail.Credentials.v1");

    /// <summary>
    /// 改名前的附加熵：产品从 MailClient 改名为 ZungMail 时熵值跟着变了，
    /// 旧账户口令是用这段熵加密的，必须留着解密，否则升级后所有账户都要重新填授权码。
    /// 新密文一律用 <see cref="Entropy"/> 写入，下一次保存账户即完成迁移。
    /// </summary>
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("MailClient.Credentials.v1");

    private readonly AesCredentialProtector _legacy;

    public DpapiCredentialProtector(IAppPaths paths)
    {
        _legacy = new AesCredentialProtector(paths);
    }

    public string Protect(string plainText)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("凭据保护依赖 Windows DPAPI。");

        var plain = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
        var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(cipher);
    }

    public string Unprotect(string cipherText)
    {
        var value = cipherText ?? string.Empty;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // 历史 AES-GCM 密文：保持可读，下次保存时由 Protect 改写为 DPAPI。
            return _legacy.Unprotect(value);
        }

        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("凭据保护依赖 Windows DPAPI。");

        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(value[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("密文不是合法的 Base64。", ex);
        }

        if (cipher.Length == 0)
        {
            throw new CryptographicException("密文长度不足，无法解密。");
        }

        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            // 改名前的密文：用旧熵再试一次。仍失败才把异常抛给调用方（按「口令不可用」处理）。
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(cipher, LegacyEntropy, DataProtectionScope.CurrentUser));
        }
    }
}
