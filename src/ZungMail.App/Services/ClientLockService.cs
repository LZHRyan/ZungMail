using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 客户端锁定：口令以 PBKDF2 派生后再经 DPAPI 保护写入设置，明文不落盘。
/// 锁定状态只在内存中维护（重启即视为已解锁，与 Foxmail 一致）；系统空闲达到设定分钟数时自动锁定。
/// </summary>
public sealed class ClientLockService
{
    /// <summary>允许的自动锁定分钟数上限，防止设置文件写入异常值。</summary>
    public const int MaxAutoLockMinutes = 240;

    private readonly ISettingsStore _settings;
    private readonly ICredentialProtector _protector;
    private readonly ILogger<ClientLockService> _logger;
    private bool _locked;

    public ClientLockService(
        ISettingsStore settings,
        ICredentialProtector protector,
        ILogger<ClientLockService> logger)
    {
        _settings = settings;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>锁定状态变化（true = 已锁定）。事件在调用线程触发。</summary>
    public event EventHandler<bool>? LockStateChanged;

    public bool IsLocked => _locked;

    /// <summary>是否已启用锁定（开关打开且口令可解密）。</summary>
    public bool IsConfigured => _settings.Current.ClientLockEnabled && ReadSecret() is not null;

    public int AutoLockMinutes => _settings.Current.AutoLockMinutes;

    /// <summary>设置或修改口令并启用锁定。</summary>
    public async Task<(bool Ok, string Message)> SetPasswordAsync(string? password, CancellationToken ct = default)
    {
        if (!ClientLockSecret.IsAcceptablePassword(password))
        {
            return (false, $"密码至少 {ClientLockSecret.MinPasswordLength} 位");
        }

        try
        {
            var wrapped = _protector.Protect(ClientLockSecret.Create(password!));
            var settings = _settings.Current;
            settings.ClientLockSecret = wrapped;
            settings.ClientLockEnabled = true;
            settings.AutoLockMinutes = Math.Clamp(settings.AutoLockMinutes, 0, MaxAutoLockMinutes);
            await _settings.SaveAsync(ct).ConfigureAwait(false);
            return (true, "已启用客户端锁定");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存锁定密码失败");
            return (false, "保存密码失败：" + ex.Message);
        }
    }

    /// <summary>关闭锁定并清除已保存的口令。</summary>
    public async Task<(bool Ok, string Message)> DisableAsync(CancellationToken ct = default)
    {
        try
        {
            var settings = _settings.Current;
            settings.ClientLockEnabled = false;
            settings.ClientLockSecret = "";
            settings.AutoLockMinutes = 0;
            await _settings.SaveAsync(ct).ConfigureAwait(false);

            SetLocked(false);
            return (true, "已关闭客户端锁定");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭客户端锁定失败");
            return (false, "关闭锁定失败：" + ex.Message);
        }
    }

    /// <summary>设置空闲自动锁定分钟数（0 表示不自动锁定）。</summary>
    public async Task<(bool Ok, string Message)> SetAutoLockMinutesAsync(int minutes, CancellationToken ct = default)
    {
        minutes = Math.Clamp(minutes, 0, MaxAutoLockMinutes);
        try
        {
            _settings.Current.AutoLockMinutes = minutes;
            await _settings.SaveAsync(ct).ConfigureAwait(false);
            return (true, minutes == 0 ? "已关闭自动锁定" : $"空闲 {minutes} 分钟后自动锁定");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存自动锁定设置失败");
            return (false, "保存失败：" + ex.Message);
        }
    }

    /// <summary>校验口令；未配置或解密失败一律视为不匹配。</summary>
    public bool Verify(string? password) => ClientLockSecret.Verify(ReadSecret(), password);

    /// <summary>立即锁定；未启用锁定时不做事。</summary>
    public void Lock()
    {
        if (!IsConfigured) return;
        SetLocked(true);
    }

    /// <summary>尝试解锁；成功返回 true，口令错误返回 false。</summary>
    public bool TryUnlock(string? password)
    {
        if (!_locked) return true;
        if (!Verify(password)) return false;

        SetLocked(false);
        return true;
    }

    /// <summary>是否应当因系统空闲而自动锁定。</summary>
    public bool ShouldAutoLock()
    {
        if (_locked || !IsConfigured) return false;

        var minutes = AutoLockMinutes;
        if (minutes <= 0) return false;

        return IdleTime.GetIdleTime() >= TimeSpan.FromMinutes(minutes);
    }

    private string? ReadSecret()
    {
        var stored = _settings.Current.ClientLockSecret;
        if (string.IsNullOrWhiteSpace(stored)) return null;

        try
        {
            return _protector.Unprotect(stored);
        }
        catch (Exception ex)
        {
            // 换用户/换机器后 DPAPI 解不开：视为未配置，避免把用户永久锁在门外。
            _logger.LogWarning(ex, "锁定口令无法解密，按未启用锁定处理");
            return null;
        }
    }

    private void SetLocked(bool value)
    {
        if (_locked == value) return;
        _locked = value;
        LockStateChanged?.Invoke(this, value);
    }
}
