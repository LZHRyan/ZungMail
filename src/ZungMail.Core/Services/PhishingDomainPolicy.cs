namespace ZungMail.Core.Services;

/// <summary>仿冒域名命中的原因。</summary>
public enum PhishingDomainReason
{
    /// <summary>形近：与自有域名只差少量字符（含相邻字符换位）。</summary>
    Lookalike = 0,

    /// <summary>混淆字符：把 0/1/5 与 o/l/s、rn 与 m 等归一后完全相同。</summary>
    Confusable = 1,

    /// <summary>后缀替换：主域相同、后缀不同（examplefreight.com → examplefreight.cn）。</summary>
    SuffixSwap = 2,

    /// <summary>子域伪装：域名里夹着自有域名，但不是它的正常子域。</summary>
    SubdomainTrick = 3
}

/// <summary>一条仿冒域名判定结论（<see cref="OwnDomain"/> 是被冒充的自有域名）。</summary>
public sealed record PhishingDomainVerdict(string OwnDomain, string SenderDomain, PhishingDomainReason Reason)
{
    /// <summary>命中原因的中文说明。</summary>
    public string ReasonText => Reason switch
    {
        PhishingDomainReason.Confusable => "把 0/1/5 之类字符换成 o/l/s 的仿冒",
        PhishingDomainReason.SuffixSwap => "主域相同、后缀不同",
        PhishingDomainReason.SubdomainTrick => "你的域名被夹在对方域名里",
        _ => "只差一个字符"
    };

    /// <summary>界面直接用的一句提示。</summary>
    public string Summary => $"与你的账户域名 {OwnDomain} 高度相似（{ReasonText}：{SenderDomain}）";
}

/// <summary>
/// 仿冒域名判定（纯函数，渲染时计算、不写库）。
/// <para>
/// 判定的输入只有「发件人域名 + 自有企业域名 + 容差」，结论任何时候算都一样，
/// 因此不做持久化：改容差或关掉开关能立刻作用于列表里的历史邮件。
/// 这与「陌生发件人」（历史事实，必须落库）是两类不同的判定。
/// </para>
/// </summary>
public static class PhishingDomainPolicy
{
    /// <summary>默认容差：与自有域名相差 1 个字符即视为仿冒。</summary>
    public const int DefaultTolerance = 1;

    /// <summary>容差夹取到 1–2：0 或异常大值都会让判定失真。</summary>
    public static int NormalizeTolerance(int tolerance) => Math.Clamp(tolerance <= 0 ? DefaultTolerance : tolerance, 1, 2);

    /// <summary>
    /// 公共邮箱服务商域名。这些域名属于「共用域名」，几十万人共用同一个域，
    /// 拿它做相似度比较只会产生噪音（`qqq.com` 之于 `qq.com`），因此既不作基准也不作比较对象。
    /// </summary>
    private static readonly HashSet<string> PublicMailDomains = new(StringComparer.Ordinal)
    {
        "qq.com", "foxmail.com", "vip.qq.com", "163.com", "126.com", "yeah.net", "188.com",
        "sina.com", "sina.cn", "sohu.com", "139.com", "189.cn", "133.cn", "wo.cn",
        "aliyun.com", "aliyun.cn", "dingtalk.com", "aliyuncs.com",
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "msn.com",
        "icloud.com", "me.com", "mac.com", "yahoo.com", "yahoo.com.cn", "ymail.com", "aol.com",
        "proton.me", "protonmail.com", "zoho.com", "gmx.com", "gmx.net", "gmx.de", "mail.com",
        "web.de", "t-online.de", "freenet.de", "orange.fr", "wanadoo.fr", "laposte.net",
        "libero.it", "virgilio.it", "tin.it", "uol.com.br", "terra.com.br", "bol.com.br",
        "yandex.ru", "ya.ru", "mail.ru", "bk.ru", "inbox.ru", "list.ru", "rambler.ru",
        "naver.com", "daum.net", "hanmail.net", "nate.com",
        "docomo.ne.jp", "ezweb.ne.jp", "softbank.ne.jp", "nifty.com", "biglobe.ne.jp", "ocn.ne.jp",
        "comcast.net", "verizon.net", "att.net", "sbcglobal.net", "btinternet.com", "virginmedia.com",
        "rediffmail.com", "sify.com", "hongkong.com"
    };

    /// <summary>常见两级后缀：解析主域（可注册域的第二级标签）时要用。</summary>
    private static readonly HashSet<string> TwoLevelSuffixes = new(StringComparer.Ordinal)
    {
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn", "ac.cn",
        "com.hk", "com.tw", "com.mo", "com.sg", "com.my", "com.ph", "com.vn", "com.pk",
        "co.uk", "co.jp", "co.kr", "co.nz", "co.in", "co.za", "co.id", "co.th",
        "com.au", "com.ru", "com.br", "com.mx", "com.ar", "com.tr", "com.sa", "com.eg",
        "net.au", "org.uk", "ac.uk", "gov.uk", "ne.jp", "or.jp", "ac.jp"
    };

    /// <summary>
    /// 账户邮箱 → 自有企业域名集合：拆域名、转小写、去掉公共邮箱域、去重。
    /// 只挂公共邮箱（qq / 163 / gmail …）的机器返回空集合，此时不会产生任何钓鱼提示。
    /// </summary>
    public static IReadOnlySet<string> OwnDomains(IEnumerable<string?> accountEmails)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var email in accountEmails)
        {
            var domain = DomainOf(email);
            if (domain.Length == 0) continue;
            if (PublicMailDomains.Contains(domain)) continue;
            domains.Add(domain);
        }

        return domains;
    }

    /// <summary>判断发件人域名是否仿冒自有域名；命中返回结论，未命中返回 null。</summary>
    public static PhishingDomainVerdict? Evaluate(
        string? senderAddress,
        IReadOnlySet<string> ownDomains,
        int tolerance = DefaultTolerance)
    {
        if (ownDomains is null || ownDomains.Count == 0) return null;

        var senderDomain = DomainOf(senderAddress);
        if (senderDomain.Length == 0) return null;

        // 公共邮箱域一律不判：同域陌生人是「陌生发件人」该管的事，不是仿冒
        if (PublicMailDomains.Contains(senderDomain)) return null;

        var limit = NormalizeTolerance(tolerance);
        var senderSld = SecondLevelLabel(senderDomain);

        foreach (var rawOwn in ownDomains)
        {
            var ownDomain = NormalizeDomain(rawOwn);
            if (ownDomain.Length == 0) continue;

            // 正常情况：完全同域（同事）、或自有域名的正常子域（mail.examplefreight.com）
            if (string.Equals(senderDomain, ownDomain, StringComparison.Ordinal)) continue;
            if (senderDomain.EndsWith("." + ownDomain, StringComparison.Ordinal)) continue;

            if (string.Equals(ConfusableNormalize(senderDomain), ConfusableNormalize(ownDomain), StringComparison.Ordinal))
            {
                return new PhishingDomainVerdict(ownDomain, senderDomain, PhishingDomainReason.Confusable);
            }

            // 主域相同、后缀不同：examplefreight.cn / examplefreight.com.cn
            var ownSld = SecondLevelLabel(ownDomain);
            if (ownSld.Length > 0 && string.Equals(senderSld, ownSld, StringComparison.Ordinal))
            {
                return new PhishingDomainVerdict(ownDomain, senderDomain, PhishingDomainReason.SuffixSwap);
            }

            // 自有域名被夹在对方域名里（examplefreight.com.secure-mail.top），但不是它的后缀
            if (ContainsOwnLabels(senderDomain, ownDomain))
            {
                return new PhishingDomainVerdict(ownDomain, senderDomain, PhishingDomainReason.SubdomainTrick);
            }

            if (EditDistance(senderDomain, ownDomain) <= limit)
            {
                return new PhishingDomainVerdict(ownDomain, senderDomain, PhishingDomainReason.Lookalike);
            }
        }

        return null;
    }

    /// <summary>取出地址里的域名（小写、去尾部点）；无法解析返回空串。</summary>
    public static string DomainOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";

        var value = address.Trim();
        var at = value.LastIndexOf('@');
        if (at < 0 || at == value.Length - 1) return "";

        return NormalizeDomain(value[(at + 1)..]);
    }

    private static string NormalizeDomain(string? domain)
    {
        var value = (domain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

        // 只接受「带点的正常域名」，排除空白、逗号之类会把比较搞乱的输入
        if (value.Length < 3 || !value.Contains('.', StringComparison.Ordinal)) return "";
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_') continue;
            return "";
        }

        return value;
    }

    /// <summary>把常见混淆写法归一：1/l 与 i、0 与 o、5 与 s、rn 与 m、vv 与 w、cl 与 d…</summary>
    internal static string ConfusableNormalize(string domain)
    {
        var value = domain.ToLowerInvariant();
        value = value.Replace("rn", "m", StringComparison.Ordinal)
                     .Replace("vv", "w", StringComparison.Ordinal)
                     .Replace("cl", "d", StringComparison.Ordinal);

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = chars[i] switch
            {
                '0' => 'o',
                '1' => 'l',
                'i' => 'l',
                '5' => 's',
                '3' => 'e',
                '9' => 'g',
                '8' => 'b',
                '7' => 't',
                '4' => 'a',
                '$' => 's',
                _ => chars[i]
            };
        }

        return new string(chars);
    }

    /// <summary>主域：可注册域的第二级标签（examplefreight.com.cn → examplefreight）。</summary>
    private static string SecondLevelLabel(string domain)
    {
        var labels = domain.Split('.');
        if (labels.Length < 2) return "";

        var lastTwo = labels[^2] + "." + labels[^1];
        if (TwoLevelSuffixes.Contains(lastTwo))
        {
            return labels.Length >= 3 ? labels[^3] : "";
        }

        return labels[^2];
    }

    /// <summary>自有域名的标签序列是否出现在发件人域名里、但不是作为后缀出现。</summary>
    private static bool ContainsOwnLabels(string senderDomain, string ownDomain)
    {
        var senderLabels = senderDomain.Split('.');
        var ownLabels = ownDomain.Split('.');
        if (ownLabels.Length == 0 || senderLabels.Length <= ownLabels.Length) return false;

        for (var start = 0; start + ownLabels.Length <= senderLabels.Length; start++)
        {
            var matched = true;
            for (var i = 0; i < ownLabels.Length; i++)
            {
                if (string.Equals(senderLabels[start + i], ownLabels[i], StringComparison.Ordinal)) continue;
                matched = false;
                break;
            }

            if (!matched) continue;

            // 作为后缀命中 = 正常子域（前面已排除），这里只关心「夹在中间」的情况
            if (start + ownLabels.Length == senderLabels.Length) continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 最优字符串对齐距离（含相邻字符换位）。域名长度通常 10–20，直接跑完整 DP 即可。
    /// </summary>
    internal static int EditDistance(string left, string right)
    {
        var a = left ?? string.Empty;
        var b = right ?? string.Empty;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous2 = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);

                // 相邻字符换位（exmaplefreight ↔ examplefreight）
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, previous2[j - 2] + 1);
                }

                current[j] = value;
            }

            var swap = previous2;
            previous2 = previous;
            previous = current;
            current = swap;
        }

        return previous[b.Length];
    }
}
