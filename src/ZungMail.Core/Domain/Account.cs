namespace ZungMail.Core.Domain;

/// <summary>邮箱账户配置（FR-01）。Secret 仅以明文存在于内存，落库前必须经 <see cref="Abstractions.ICredentialProtector"/> 加密。</summary>
public sealed class Account
{
    public long Id { get; set; }

    /// <summary>显示名称，例如「工作邮箱」。</summary>
    public string Name { get; set; } = "";

    /// <summary>邮箱地址。</summary>
    public string Email { get; set; } = "";

    /// <summary>发信时使用的显示名，留空则回落到 <see cref="Name"/>。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>登录用户名，多数服务商等于邮箱地址。</summary>
    public string Username { get; set; } = "";

    /// <summary>授权码 / 密码明文（仅内存）。</summary>
    public string Secret { get; set; } = "";

    public AuthKind AuthKind { get; set; } = AuthKind.AuthCode;

    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public SecureMode ImapSecure { get; set; } = SecureMode.Ssl;

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public SecureMode SmtpSecure { get; set; } = SecureMode.Ssl;

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 主账户标记：多账户时同一时刻只有一个主账户。同步顺序与账户列表都把它排在最前，
    /// 其余账户（副账户）排在后面（见 <see cref="Abstractions.IAccountRepository.SetPrimaryAsync"/>）。
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>收取策略：默认继承设置页的「默认收取策略」，账户可单独覆盖。</summary>
    public FetchPolicy FetchPolicy { get; set; } = FetchPolicy.Inherit;

    public int SortOrder { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>界面标识色。</summary>
    public string Color { get; set; } = "#2563eb";

    /// <summary>最近一次连接状态说明（运行时字段，不落库）。</summary>
    public string ConnectionStatus { get; set; } = "未验证";

    public string Initials => DisplayFormat.Initial(Name);

    public string SenderDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    public bool HasCompleteConfig =>
        !string.IsNullOrWhiteSpace(ImapHost) &&
        !string.IsNullOrWhiteSpace(SmtpHost) &&
        !string.IsNullOrWhiteSpace(Username);
}
