using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZungMail.UpdateServer.Storage;

namespace ZungMail.UpdateServer.Security;

/// <summary>管理员口令来源，管理页据此提示运维去哪里取初始口令。</summary>
public enum AdminPasswordSource
{
    Environment,
    Stored,
    Generated
}

public sealed record ChangePasswordResult(bool Success, string Message);

/// <summary>
/// 管理员认证：
/// <list type="bullet">
/// <item>口令来源优先级：环境变量（只读）→ 数据目录里的 PBKDF2 哈希 → 首次启动随机生成。</item>
/// <item>会话是进程内随机 token，放在 HttpOnly + SameSite=Strict 的 Cookie 里，滑动续期。</item>
/// <item>登录按来源 IP 计失败次数，超过阈值进入冷却，避免在线爆破。</item>
/// </list>
/// </summary>
public sealed class AdminAuthService
{
    public const string CookieName = "mc_update_session";
    private const int PasswordSaltBytes = 16;
    private const int PasswordHashBytes = 32;
    private const int DefaultIterations = 210_000;
    private const string GeneratedPasswordAlphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly UpdateServerOptions _options;
    private readonly ReleaseStore _store;
    private readonly ILogger<AdminAuthService> _logger;
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LoginAttempts> _attempts = new(StringComparer.Ordinal);

    private byte[] _salt = [];
    private byte[] _hash = [];
    private int _iterations = DefaultIterations;

    public AdminAuthService(UpdateServerOptions options, ReleaseStore store, ILogger<AdminAuthService> logger)
    {
        _options = options;
        _store = store;
        _logger = logger;
        CredentialPath = Path.Combine(store.DataDirectory, "admin.json");
        InitialPasswordPath = Path.Combine(store.DataDirectory, "initial-admin-password.txt");
    }

    public string CredentialPath { get; }

    public string InitialPasswordPath { get; }

    public AdminPasswordSource PasswordSource { get; private set; }

    /// <summary>环境变量提供口令时不允许在线改口令（重启后会被环境变量覆盖，容易造成误解）。</summary>
    public bool CanChangePassword => PasswordSource != AdminPasswordSource.Environment;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_options.AdminPassword))
        {
            SetPassword(_options.AdminPassword);
            PasswordSource = AdminPasswordSource.Environment;
            return;
        }

        if (await TryLoadStoredAsync(ct))
        {
            PasswordSource = AdminPasswordSource.Stored;
            return;
        }

        var generated = GeneratePassword(20);
        SetPassword(generated);
        PasswordSource = AdminPasswordSource.Generated;
        await PersistAsync(ct);

        try
        {
            await File.WriteAllTextAsync(
                InitialPasswordPath,
                "ZungMail 升级服务初始管理员口令（登录后请立即修改，本文件会在修改口令后删除）：" +
                Environment.NewLine + generated + Environment.NewLine,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入初始口令文件失败");
        }

        _logger.LogWarning(
            "未提供 UPDATESERVER_ADMIN_PASSWORD，已生成初始管理员口令（见 {Path}），首次登录后请立即修改。",
            InitialPasswordPath);
    }

    public bool VerifyPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || _hash.Length == 0) return false;
        var candidate = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), _salt, _iterations, HashAlgorithmName.SHA256, PasswordHashBytes);
        return CryptographicOperations.FixedTimeEquals(candidate, _hash);
    }

    public async Task<ChangePasswordResult> ChangePasswordAsync(string? current, string? next, CancellationToken ct = default)
    {
        if (!CanChangePassword)
        {
            return new ChangePasswordResult(false, "当前口令来自环境变量 UPDATESERVER_ADMIN_PASSWORD，请修改环境变量后重启服务。");
        }

        if (!VerifyPassword(current)) return new ChangePasswordResult(false, "当前口令不正确。");
        if (string.IsNullOrWhiteSpace(next) || next.Length < 8)
        {
            return new ChangePasswordResult(false, "新口令至少 8 个字符。");
        }

        SetPassword(next);
        await PersistAsync(ct);
        RevokeAllSessions();

        try
        {
            if (File.Exists(InitialPasswordPath)) File.Delete(InitialPasswordPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除初始口令文件失败");
        }

        return new ChangePasswordResult(true, "口令已更新，请用新口令重新登录。");
    }

    public string CreateSession()
    {
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = new SessionEntry(DateTimeOffset.UtcNow.Add(_options.SessionLifetime));
        return token;
    }

    /// <summary>校验会话并滑动续期；token 为空或过期返回 false。</summary>
    public bool ValidateSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!_sessions.TryGetValue(token, out var entry)) return false;

        var now = DateTimeOffset.UtcNow;
        if (entry.ExpiresAt <= now)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        entry.ExpiresAt = now.Add(_options.SessionLifetime);
        CleanupExpiredSessions(now);
        return true;
    }

    public DateTimeOffset? GetSessionExpiry(string? token)
        => token is not null && _sessions.TryGetValue(token, out var entry) ? entry.ExpiresAt : null;

    public void RevokeSession(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    public void RevokeAllSessions() => _sessions.Clear();

    public bool IsLockedOut(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_attempts.TryGetValue(key, out var attempts) || attempts.LockedUntil is not { } until) return false;

        var now = DateTimeOffset.UtcNow;
        if (until <= now)
        {
            attempts.LockedUntil = null;
            attempts.Count = 0;
            return false;
        }

        retryAfter = until - now;
        return true;
    }

    public void RegisterFailure(string key)
    {
        var attempts = _attempts.GetOrAdd(key, _ => new LoginAttempts());
        attempts.Count++;
        if (attempts.Count >= _options.LoginFailureLimit)
        {
            attempts.LockedUntil = DateTimeOffset.UtcNow.Add(_options.LoginLockout);
            attempts.Count = 0;
        }
    }

    public void ResetFailures(string key) => _attempts.TryRemove(key, out _);

    private async Task<bool> TryLoadStoredAsync(CancellationToken ct)
    {
        if (!File.Exists(CredentialPath)) return false;

        try
        {
            await using var stream = File.OpenRead(CredentialPath);
            var file = await JsonSerializer.DeserializeAsync<AdminCredentialFile>(stream, JsonOptions, ct);
            if (file is null || file.Iterations <= 0) return false;

            var salt = Convert.FromBase64String(file.Salt);
            var hash = Convert.FromBase64String(file.Hash);
            if (salt.Length == 0 || hash.Length == 0) return false;

            _salt = salt;
            _hash = hash;
            _iterations = file.Iterations;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取管理员口令文件失败，将生成新的初始口令");
            return false;
        }
    }

    private void SetPassword(string password)
    {
        _salt = RandomNumberGenerator.GetBytes(PasswordSaltBytes);
        _iterations = DefaultIterations;
        _hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), _salt, _iterations, HashAlgorithmName.SHA256, PasswordHashBytes);
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_store.DataDirectory);
        var file = new AdminCredentialFile
        {
            Algorithm = "PBKDF2-SHA256",
            Iterations = _iterations,
            Salt = Convert.ToBase64String(_salt),
            Hash = Convert.ToBase64String(_hash),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var temp = CredentialPath + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, file, JsonOptions, ct);
            await stream.FlushAsync(ct);
        }

        File.Move(temp, CredentialPath, overwrite: true);
    }

    private void CleanupExpiredSessions(DateTimeOffset now)
    {
        if (_sessions.Count < 64) return;
        foreach (var pair in _sessions)
        {
            if (pair.Value.ExpiresAt <= now) _sessions.TryRemove(pair.Key, out _);
        }
    }

    private static string GeneratePassword(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        var builder = new StringBuilder(length);
        foreach (var value in bytes)
        {
            builder.Append(GeneratedPasswordAlphabet[value % GeneratedPasswordAlphabet.Length]);
        }

        return builder.ToString();
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class SessionEntry(DateTimeOffset expiresAt)
    {
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }

    private sealed class LoginAttempts
    {
        public int Count { get; set; }

        public DateTimeOffset? LockedUntil { get; set; }
    }

    private sealed class AdminCredentialFile
    {
        [JsonPropertyName("algorithm")]
        public string Algorithm { get; set; } = "PBKDF2-SHA256";

        [JsonPropertyName("iterations")]
        public int Iterations { get; set; }

        [JsonPropertyName("salt")]
        public string Salt { get; set; } = "";

        [JsonPropertyName("hash")]
        public string Hash { get; set; } = "";

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
