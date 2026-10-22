using System.Security.Cryptography;
using System.Text;
using ZungMail.Core.Abstractions;

namespace ZungMail.Infrastructure.Security;

/// <summary>
/// AES-256-GCM 凭据保护（NFR-S2）。密文格式：Base64(nonce(12) || tag(16) || ciphertext)。
/// <para>
/// 已由 <see cref="DpapiCredentialProtector"/> 取代，仅保留解密能力用于历史数据平滑迁移：
/// 旧密文经 <see cref="DpapiCredentialProtector.Unprotect"/> 识别后回落到本实现，
/// 下次保存时会以 DPAPI 格式重新写入。新数据不得再使用本实现加密。
/// </para>
/// </summary>
public sealed class AesCredentialProtector : ICredentialProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int SaltSize = 32;
    private const int Iterations = 100_000;

    private readonly string _keyFilePath;
    private readonly Lazy<byte[]> _key;

    public AesCredentialProtector(IAppPaths paths)
    {
        _keyFilePath = Path.Combine(paths.RootDirectory, "credential.key");
        _key = new Lazy<byte[]>(DeriveKey, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Protect(string plainText)
    {
        var plain = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(_key.Value, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        var buffer = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, buffer, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, buffer, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, buffer, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(buffer);
    }

    public string Unprotect(string cipherText)
    {
        byte[] buffer;
        try
        {
            buffer = Convert.FromBase64String(cipherText ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("密文不是合法的 Base64。", ex);
        }

        if (buffer.Length < NonceSize + TagSize + 1)
        {
            throw new CryptographicException("密文长度不足，无法解密。");
        }

        var nonce = buffer.AsSpan(0, NonceSize);
        var tag = buffer.AsSpan(NonceSize, TagSize);
        var cipher = buffer.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(_key.Value, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            throw;
        }
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] DeriveKey()
    {
        var salt = LoadOrCreateSalt();
        var password = $"{Environment.MachineName}|{Environment.UserName}|{Convert.ToBase64String(salt)}";
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
    }

    private byte[] LoadOrCreateSalt()
    {
        if (File.Exists(_keyFilePath))
        {
            var existing = File.ReadAllBytes(_keyFilePath);
            if (existing.Length > 0) return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_keyFilePath)!);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        File.WriteAllBytes(_keyFilePath, salt);
        return salt;
    }
}
