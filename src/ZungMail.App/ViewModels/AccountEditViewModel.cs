using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 账户配置（FR-01 / FR-02）：录入邮箱地址后自动匹配服务商预设，
/// 「测试连接」即时验证 IMAP/SMTP，凭据交由仓储加密落库（NFR-S2）。
/// </summary>
public sealed partial class AccountEditViewModel : ObservableObject
{
    private readonly IAccountRepository _accounts;
    private readonly ISettingsStore _settings;
    private readonly IServerConfigResolver _presets;
    private readonly IMailSyncService _sync;
    private readonly ILogger<AccountEditViewModel> _logger;
    private readonly Account _model;

    /// <summary>已按预设填充过服务器配置，用户再次修改邮箱时不再覆盖。</summary>
    private bool _presetApplied;

    public AccountEditViewModel(
        IAccountRepository accounts,
        ISettingsStore settings,
        IServerConfigResolver presets,
        IMailSyncService sync,
        ILogger<AccountEditViewModel> logger,
        Account? account = null)
    {
        _accounts = accounts;
        _settings = settings;
        _presets = presets;
        _sync = sync;
        _logger = logger;

        IsNew = account is null;
        _model = account ?? new Account();

        Title = IsNew ? "添加邮箱账户" : "编辑邮箱账户";

        Name = _model.Name;
        Email = _model.Email;
        DisplayName = _model.DisplayName;
        Username = _model.Username;
        Secret = _model.Secret;
        CredentialKind = _model.AuthKind;
        ImapHost = _model.ImapHost;
        ImapPort = _model.ImapPort;
        ImapSecure = _model.ImapSecure;
        SmtpHost = _model.SmtpHost;
        SmtpPort = _model.SmtpPort;
        SmtpSecure = _model.SmtpSecure;
        IsEnabled = _model.IsEnabled;
        SelectedFetchPolicy = _model.FetchPolicy;
        Color = _model.Color;

        _presetApplied = !IsNew && !string.IsNullOrWhiteSpace(_model.ImapHost);
        if (_presetApplied) PresetName = _presets.Resolve(_model.Email)?.DisplayName ?? "";
    }

    public string Title { get; }

    public bool IsNew { get; }

    public IReadOnlyList<AuthKind> AuthKinds { get; } =
        new[] { AuthKind.AuthCode, AuthKind.Password, AuthKind.OAuth2 };

    public IReadOnlyList<SecureMode> SecureModes { get; } =
        new[] { SecureMode.Ssl, SecureMode.StartTls, SecureMode.None };

    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _secret = "";
    [ObservableProperty] private AuthKind _credentialKind = AuthKind.AuthCode;
    [ObservableProperty] private string _imapHost = "";
    [ObservableProperty] private int _imapPort = 993;
    [ObservableProperty] private SecureMode _imapSecure = SecureMode.Ssl;
    [ObservableProperty] private string _smtpHost = "";
    [ObservableProperty] private int _smtpPort = 465;
    [ObservableProperty] private SecureMode _smtpSecure = SecureMode.Ssl;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private FetchPolicy _selectedFetchPolicy = FetchPolicy.Inherit;

    /// <summary>收取设置的三个可选档位（「继承默认」始终在第一项）。</summary>
    public IReadOnlyList<FetchPolicy> FetchPolicies { get; } = new[]
    {
        FetchPolicy.Inherit, FetchPolicy.Full, FetchPolicy.BodyOnly, FetchPolicy.HeadersOnly
    };

    /// <summary>下拉框下方的说明：继承时把实际生效的默认策略写出来。</summary>
    public string FetchPolicyHint => SelectedFetchPolicy switch
    {
        FetchPolicy.Full => "正文与附件全部下载，双击邮件即可离线阅读（大附件账户会占较多磁盘）。",
        FetchPolicy.BodyOnly => "只下载正文，附件在需要时单独下载；正文里的内嵌图片仍会下载。",
        FetchPolicy.HeadersOnly => "只同步邮件头，正文与附件都在阅读时按需下载，最省流量。",
        _ => $"跟随设置页的「默认收取策略」（当前：{DescribeDefaultPolicy()}）。"
    };

    private string DescribeDefaultPolicy() => _settings.Current.BodyPrefetchDays switch
    {
        <= 0 => "仅收取邮件头",
        AppSettings.BodyPrefetchAllDays => "完整收取",
        var days => $"最近 {days} 天收取正文"
    };

    partial void OnSelectedFetchPolicyChanged(FetchPolicy value) => OnPropertyChanged(nameof(FetchPolicyHint));
    [ObservableProperty] private string _color = "#2563eb";
    [ObservableProperty] private string _presetName = "";
    [ObservableProperty] private string _statusText = "填写邮箱地址后自动匹配服务器配置";
    [ObservableProperty] private bool _isBusy;

    public string SecretHint => CredentialKind switch
    {
        AuthKind.AuthCode => "QQ / 163 / 263 等邮箱需在网页端开启 IMAP 并生成授权码",
        AuthKind.OAuth2 => "粘贴 OAuth2 访问令牌（Access Token）",
        _ => "邮箱登录密码（部分服务商已停用，建议使用授权码）"
    };

    public string InitialsPreview => DisplayFormat.Initial(string.IsNullOrWhiteSpace(Name) ? Email : Name);

    public string ColorPreview => string.IsNullOrWhiteSpace(Color) ? "#2563eb" : Color;

    /// <summary>常用标识色：一眼可点，特殊颜色再打开色盘。</summary>
    public IReadOnlyList<string> PresetColors { get; } = new[]
    {
        "#2563eb", "#0ea5e9", "#0f7b6c", "#16a34a",
        "#b4681f", "#c24e7d", "#7c3aed", "#64748b"
    };

    /// <summary>点选常用色（色盘之外的快捷入口）。</summary>
    [RelayCommand]
    private void PickPresetColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex)) Color = hex;
    }

    partial void OnCredentialKindChanged(AuthKind value) => OnPropertyChanged(nameof(SecretHint));

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(InitialsPreview));

    partial void OnColorChanged(string value) => OnPropertyChanged(nameof(ColorPreview));

    /// <summary>邮箱地址变更时按域名匹配预置服务器（FR-02）。</summary>
    partial void OnEmailChanged(string value)
    {
        OnPropertyChanged(nameof(InitialsPreview));

        if (_presetApplied) return;
        var preset = _presets.Resolve(value);
        if (preset is null) return;

        ImapHost = preset.ImapHost;
        ImapPort = preset.ImapPort;
        ImapSecure = preset.ImapSecure;
        SmtpHost = preset.SmtpHost;
        SmtpPort = preset.SmtpPort;
        SmtpSecure = preset.SmtpSecure;
        if (string.IsNullOrWhiteSpace(Username)) Username = value;
        if (string.IsNullOrWhiteSpace(Name)) Name = preset.DisplayName;

        _presetApplied = true;
        PresetName = preset.DisplayName;
        StatusText = $"已匹配「{preset.DisplayName}」配置，请填写凭据后测试连接";
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsBusy) return;

        var account = Capture();
        if (!Validate(account, out var error))
        {
            StatusText = error;
            return;
        }

        IsBusy = true;
        StatusText = "正在连接 IMAP / SMTP…";
        try
        {
            var ok = await _sync.TestConnectionAsync(account);
            StatusText = ok ? "连接成功，IMAP 与 SMTP 均可用" : "连接失败，请检查服务器地址、端口与凭据";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "测试账户连接失败：{Email}", account.Email);
            StatusText = "连接失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy) return;

        var account = Capture();
        if (!Validate(account, out var error))
        {
            StatusText = error;
            return;
        }

        IsBusy = true;
        try
        {
            account.SortOrder = _model.SortOrder;
            account.CreatedUtc = _model.CreatedUtc;
            account.ConnectionStatus = "未验证";

            if (IsNew) await _accounts.AddAsync(account);
            else await _accounts.UpdateAsync(account);

            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存账户失败：{Email}", account.Email);
            StatusText = "保存失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    /// <summary>把界面字段写回账户模型（Secret 仍为明文，由仓储负责加密）。</summary>
    private Account Capture()
    {
        _model.Name = Name.Trim();
        _model.Email = Email.Trim();
        _model.DisplayName = DisplayName.Trim();
        _model.Username = string.IsNullOrWhiteSpace(Username) ? Email.Trim() : Username.Trim();
        _model.Secret = Secret;
        _model.AuthKind = CredentialKind;
        _model.ImapHost = ImapHost.Trim();
        _model.ImapPort = ImapPort;
        _model.ImapSecure = ImapSecure;
        _model.SmtpHost = SmtpHost.Trim();
        _model.SmtpPort = SmtpPort;
        _model.SmtpSecure = SmtpSecure;
        _model.IsEnabled = IsEnabled;
        _model.FetchPolicy = SelectedFetchPolicy;
        _model.Color = string.IsNullOrWhiteSpace(Color) ? "#2563eb" : Color.Trim();
        return _model;
    }

    private static bool Validate(Account account, out string error)
    {
        if (string.IsNullOrWhiteSpace(account.Email) || !account.Email.Contains('@'))
        {
            error = "请填写有效的邮箱地址";
            return false;
        }
        if (string.IsNullOrWhiteSpace(account.Name))
        {
            error = "请填写账户名称（如「工作邮箱」）";
            return false;
        }
        if (string.IsNullOrWhiteSpace(account.ImapHost) || string.IsNullOrWhiteSpace(account.SmtpHost))
        {
            error = "请填写 IMAP 与 SMTP 服务器地址";
            return false;
        }
        if (account.ImapPort is <= 0 or > 65535 || account.SmtpPort is <= 0 or > 65535)
        {
            error = "端口需在 1 - 65535 之间";
            return false;
        }
        if (string.IsNullOrWhiteSpace(account.Username))
        {
            error = "请填写登录用户名";
            return false;
        }
        if (string.IsNullOrEmpty(account.Secret))
        {
            error = "请填写授权码 / 密码";
            return false;
        }

        error = "";
        return true;
    }
}
