using System.Text;
using System.Text.RegularExpressions;
using ZungMail.Core.Abstractions;

namespace ZungMail.Core.Services;

/// <summary>清洗后的摘要输入：纯文本 + 是否被截断 + 原始长度。</summary>
public sealed record MailSummaryInput(string Text, bool Truncated, int OriginalChars)
{
    public static MailSummaryInput Empty { get; } = new("", false, 0);

    public bool HasContent => Text.Length > 0;
}

/// <summary>
/// 把邮件正文整理成「适合小模型读」的一段文字：
/// 纯文本优先（HTML 兜底）→ 去历史引用 → 去签名 → 空白归一 → 按上限截断。
/// 这一步决定摘要质量的一半，也避免把十层引用一次性喂给模型。
/// </summary>
public static partial class MailSummaryInputBuilder
{
    /// <summary>默认覆盖上限：12000 字（按 4000 字一段切 3 段）。</summary>
    public const int DefaultMaxChars = 12000;

    public static int ClampMaxChars(int maxChars)
        => Math.Clamp(maxChars <= 0 ? DefaultMaxChars : maxChars,
            Domain.MailSummarySettings.MinInputChars, Domain.MailSummarySettings.MaxInputChars);

    /// <param name="bodyText">库里的纯文本正文（优先）。</param>
    /// <param name="bodyHtml">纯文本为空时的 HTML 正文。</param>
    public static MailSummaryInput Build(string? bodyText, string? bodyHtml, int maxChars = DefaultMaxChars)
    {
        var text = NormalizeNewlines(bodyText);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = NormalizeNewlines(HtmlTextExtractor.ToPlainText(bodyHtml));
        }

        if (string.IsNullOrWhiteSpace(text)) return MailSummaryInput.Empty;

        var originalLength = text.Length;

        text = StripQuotedHistory(text);
        text = StripSignature(text);
        text = CollapseWhitespace(text).Trim();

        if (text.Length == 0) return MailSummaryInput.Empty;

        var truncated = false;
        var limit = ClampMaxChars(maxChars);
        if (text.Length > limit)
        {
            text = text[..limit];
            truncated = true;
        }

        return new MailSummaryInput(text, truncated, originalLength);
    }

    /// <summary>统一换行，去掉零宽字符与不换行空格。</summary>
    private static string NormalizeNewlines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ')
            .Replace("\u200B", "");
    }

    /// <summary>
    /// 去掉更早的往来引用：从**第一条**引用分隔标记处截断，并丢掉以 <c>&gt;</c> 开头的引用行。
    /// 只保留最新一层，避免摘要覆盖三个月前的对话。
    /// </summary>
    private static string StripQuotedHistory(string text)
    {
        var cut = text.Length;

        foreach (var marker in QuoteMarkers())
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 0 && index < cut) cut = index;
        }

        // 「On 2026-10-20 ... wrote:」/「在 2026年10月20日 ... 写道：」两种常见引用头
        var headerMatch = QuotedHeaderRegex.Match(text);
        if (headerMatch.Success && headerMatch.Index > 0 && headerMatch.Index < cut)
        {
            cut = headerMatch.Index;
        }

        // 分隔符出现在开头附近说明整封就是转发/纯引用：宁可不裁，也别把正文裁空
        if (cut < text.Length && cut > text.Length / 10)
        {
            text = text[..cut];
        }

        var builder = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith('>')) continue;
            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static string[] QuoteMarkers() =>
    [
        "-----Original Message-----",
        "----- 原始邮件 -----",
        "------------------ 原始邮件",
        "-----邮件原件-----",
        "----- Forwarded message -----"
    ];

    // 说明：这条模式含惰性量词（.\{6,120\}?），SDK 8.0.404 的正则源生成器会生成带 CS0165
    //（lazyloop_iteration 未赋值）的代码导致编译失败，因此改用运行时编译；表达式与行为完全不变。
    private static readonly Regex QuotedHeaderRegex = new(
        @"(?m)^(?:On\s.{6,120}?wrote:|在\s.{2,40}?写道[:：])\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>去掉尾部签名块（--、此致、Best regards 等），只在正文后半段生效。</summary>
    private static string StripSignature(string text)
    {
        var lines = text.Split('\n');
        // 从后半段开始找签名起始标记：太靠前会把正文里的「--」误判成签名
        var from = Math.Max(0, (int)(lines.Length * 0.4));
        for (var i = from; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (SignatureStarters().Any(starter => line.StartsWith(starter, StringComparison.OrdinalIgnoreCase)))
            {
                return string.Join('\n', lines.Take(i));
            }
        }

        return text;
    }

    private static string[] SignatureStarters() =>
    [
        "--",
        "此致",
        "顺祝",
        "Best regards",
        "Best Regards",
        "Regards",
        "Thanks & Regards",
        "Sent from my",
        "发送自"
    ];

    /// <summary>折叠连续空行、去行尾空白、制表符转空格。</summary>
    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var blankRun = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Replace('\t', ' ').TrimEnd();
            if (line.Length == 0)
            {
                blankRun++;
                if (blankRun > 1) continue;
            }
            else
            {
                blankRun = 0;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    /// <summary>把一句话里的连续空白压成一个空格（摘要输出归一用）。</summary>
    public static string Squash(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return MultipleSpaces().Replace(text.Trim(), " ");
    }

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleSpaces();
}
