using System.Globalization;
using ZungMail.Core.Domain;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 设置 → 账户 列表的卡片数据：把账户配置、运行状态、本地邮件统计与签名状态
/// 压成一组只读展示字段，避免在 XAML 里堆转换器，也避免把展示属性写进领域模型。
/// </summary>
public sealed class AccountCardItem
{
    private AccountCardItem(
        Account account,
        int totalMessages,
        int unreadMessages,
        bool signatureEnabled)
    {
        Account = account;
        TotalMessages = totalMessages;
        UnreadMessages = unreadMessages;
        SignatureEnabled = signatureEnabled;
    }

    /// <summary>原始账户对象：选中卡片后仍用它执行编辑 / 同步 / 停用等命令。</summary>
    public Account Account { get; }

    public int TotalMessages { get; }

    public int UnreadMessages { get; }

    public bool SignatureEnabled { get; }

    public long Id => Account.Id;

    public string Name => string.IsNullOrWhiteSpace(Account.Name) ? Account.Email : Account.Name;

    public string Email => Account.Email;

    /// <summary>账户名本身就是邮箱时不重复展示一行同样的地址。</summary>
    public bool ShowEmailLine => !string.Equals(Name, Email, StringComparison.OrdinalIgnoreCase);

    /// <summary>登录用户名与邮箱地址不同时才额外展示（企业邮箱常见）。</summary>
    public bool HasCustomUsername =>
        !string.IsNullOrWhiteSpace(Account.Username) &&
        !string.Equals(Account.Username, Account.Email, StringComparison.OrdinalIgnoreCase);

    public string UsernameText => "登录名 " + Account.Username;

    public string Initials => Account.Initials;

    public string Color => string.IsNullOrWhiteSpace(Account.Color) ? "#2563eb" : Account.Color;

    public bool IsEnabled => Account.IsEnabled;

    public bool HasCompleteConfig => Account.HasCompleteConfig;

    /// <summary>发件显示名与账户名不同时才额外展示一行。</summary>
    public bool HasSenderDisplay =>
        !string.IsNullOrWhiteSpace(Account.DisplayName) &&
        !string.Equals(Account.DisplayName, Account.Name, StringComparison.Ordinal);

    public string SenderDisplayText => "发件显示：" + Account.SenderDisplayName;

    public string ImapText => FormatEndpoint("IMAP", Account.ImapHost, Account.ImapPort, Account.ImapSecure);

    public string SmtpText => FormatEndpoint("SMTP", Account.SmtpHost, Account.SmtpPort, Account.SmtpSecure);

    public string AuthText => Account.AuthKind switch
    {
        AuthKind.Password => "密码登录",
        AuthKind.OAuth2 => "OAuth2",
        _ => "授权码"
    };

    public bool HasInsecureTransport =>
        Account.ImapSecure == SecureMode.None || Account.SmtpSecure == SecureMode.None;

    public string StatusText => !Account.IsEnabled
        ? "已停用"
        : Account.HasCompleteConfig ? "使用中" : "配置不完整";

    public string LocalStatsText
    {
        get
        {
            if (TotalMessages <= 0) return "本地暂无邮件";
            var total = TotalMessages.ToString("N0", CultureInfo.CurrentCulture);
            if (UnreadMessages <= 0) return $"本地 {total} 封 · 无未读";
            var unread = UnreadMessages.ToString("N0", CultureInfo.CurrentCulture);
            return $"本地 {total} 封 · 未读 {unread}";
        }
    }

    public string SignatureText => SignatureEnabled ? "签名已启用" : "签名未启用";

    /// <summary>主账户：同步与列表排序都排在最前。</summary>
    public bool IsPrimary => Account.IsPrimary;

    /// <summary>卡片上的优先级标签：主账户 / 副账户。</summary>
    public string PriorityText => Account.IsPrimary ? "主账户" : "副账户";

    /// <summary>构造卡片：签名状态来自当前设置（单账户设置优先，旧版全局签名兜底）。</summary>
    public static AccountCardItem Create(
        Account account,
        int totalMessages,
        int unreadMessages,
        AppSettings settings)
    {
        var signatureEnabled = !string.IsNullOrWhiteSpace(settings.ResolveSignatureHtml(account.Id));
        return new AccountCardItem(account, Math.Max(totalMessages, 0), Math.Max(unreadMessages, 0), signatureEnabled);
    }

    private static string FormatEndpoint(string kind, string host, int port, SecureMode secure)
    {
        var target = string.IsNullOrWhiteSpace(host) ? "未配置" : $"{host}:{port}";
        var mode = secure switch
        {
            SecureMode.Ssl => "SSL",
            SecureMode.StartTls => "STARTTLS",
            _ => "不加密"
        };
        return $"{kind} {target} · {mode}";
    }
}
