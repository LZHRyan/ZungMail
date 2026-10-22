using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>
/// 内置服务器配置模板（FR-02）。覆盖目标邮箱（263）与国内常见邮箱，
/// 并附带 Gmail / Outlook / iCloud 等国际服务商。未命中时返回 null，界面转为手动配置。
/// </summary>
public sealed class ServerConfigResolver : IServerConfigResolver
{
    private readonly Dictionary<string, ServerPreset> _presets;

    public ServerConfigResolver()
    {
        var list = BuildPresets();
        _presets = list
            .GroupBy(p => p.Domain, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        All = list;
    }

    public IReadOnlyList<ServerPreset> All { get; }

    public ServerPreset? Resolve(string email)
    {
        var domain = DisplayFormat.DomainOf(email);
        if (domain.Length == 0) return null;

        if (_presets.TryGetValue(domain, out var exact)) return exact;

        // 企业邮箱常见子域：mail.example.com → 尝试 example.com
        var parts = domain.Split('.');
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var candidate = string.Join('.', parts[i..]);
            if (_presets.TryGetValue(candidate, out var suffix)) return suffix;
        }
        return null;
    }

    private static List<ServerPreset> BuildPresets()
    {
        static ServerPreset Ssl(string domain, string name, string imap, string smtp)
            => new(domain, name, imap, 993, SecureMode.Ssl, smtp, 465, SecureMode.Ssl);

        static ServerPreset StartTls(string domain, string name, string imap, string smtp, int smtpPort)
            => new(domain, name, imap, 993, SecureMode.Ssl, smtp, smtpPort, SecureMode.StartTls);

        return new List<ServerPreset>
        {
            // —— 国内 ——
            Ssl("263.net",  "263 邮箱",     "imap.263.net",      "smtp.263.net"),
            Ssl("263.com",  "263 企业邮箱", "imap.263.net",      "smtp.263.net"),
            Ssl("qq.com",   "QQ 邮箱",      "imap.qq.com",       "smtp.qq.com"),
            Ssl("foxmail.com", "Foxmail 邮箱", "imap.qq.com",    "smtp.qq.com"),
            Ssl("exmail.qq.com", "腾讯企业邮箱", "imap.exmail.qq.com", "smtp.exmail.qq.com"),
            Ssl("163.com",  "网易 163 邮箱", "imap.163.com",     "smtp.163.com"),
            Ssl("126.com",  "网易 126 邮箱", "imap.126.com",     "smtp.126.com"),
            Ssl("yeah.net", "网易 yeah.net", "imap.yeah.net",    "smtp.yeah.net"),
            Ssl("sina.com", "新浪邮箱",      "imap.sina.com",     "smtp.sina.com"),
            Ssl("sina.cn",  "新浪邮箱",      "imap.sina.com",     "smtp.sina.com"),
            Ssl("sohu.com", "搜狐邮箱",      "imap.sohu.com",     "smtp.sohu.com"),
            Ssl("aliyun.com", "阿里云邮箱",  "imap.aliyun.com",   "smtp.aliyun.com"),
            Ssl("aliyun.cn",  "阿里云邮箱",  "imap.aliyun.com",   "smtp.aliyun.com"),
            Ssl("139.com",  "中国移动 139",  "imap.139.com",      "smtp.139.com"),
            Ssl("189.cn",   "中国电信 189",  "imap.189.cn",       "smtp.189.cn"),
            Ssl("wo.cn",    "中国联通沃邮箱", "imap.wo.cn",       "smtp.wo.cn"),
            Ssl("21cn.com", "21CN 邮箱",     "imap.21cn.com",     "smtp.21cn.com"),
            Ssl("tom.com",  "TOM 邮箱",      "imap.tom.com",      "smtp.tom.com"),

            // —— 国际 ——
            Ssl("gmail.com",   "Gmail",     "imap.gmail.com",       "smtp.gmail.com"),
            Ssl("googlemail.com", "Gmail",  "imap.gmail.com",       "smtp.gmail.com"),
            StartTls("outlook.com", "Outlook", "outlook.office365.com", "smtp.office365.com", 587),
            StartTls("hotmail.com", "Outlook", "outlook.office365.com", "smtp.office365.com", 587),
            StartTls("live.com",    "Outlook", "outlook.office365.com", "smtp.office365.com", 587),
            Ssl("icloud.com",  "iCloud 邮箱", "imap.mail.me.com",    "smtp.mail.me.com"),
            Ssl("me.com",      "iCloud 邮箱", "imap.mail.me.com",    "smtp.mail.me.com"),
            Ssl("zoho.com",    "Zoho 邮箱",   "imap.zoho.com",       "smtp.zoho.com"),
            Ssl("yahoo.com",   "Yahoo 邮箱",  "imap.mail.yahoo.com", "smtp.mail.yahoo.com"),
            Ssl("aol.com",     "AOL 邮箱",    "imap.aol.com",        "smtp.aol.com"),
        };
    }
}
