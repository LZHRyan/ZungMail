using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>
/// 回复 / 回复全部 / 转发时，把被回复的邮件重建为**可编辑的 HTML 引用块**。
///
/// 之前这段是拼纯文本（发件人 / 时间 / 主题 + <c>BodyText</c>），所以一进写信窗口
/// 原文的表格、加粗、颜色、内嵌图片全丢。这里改为：
/// 1. 原文用 <see cref="HtmlMailDocumentBuilder.BuildEditorFragment"/> 清洗后原样嵌入（保留内联样式与表格）；
/// 2. 内嵌图片由 <c>file:///</c> 就地内联成 <c>data:</c>——撰写页只允许 data:/http(s)，读不到本地文件；
///    发送时 <see cref="ComposeBodyBuilder.ExtractInlineImages"/> 再把 data: 转成 cid 内嵌资源，
///    收件人也能看到图；
/// 3. 外层包 <c>.mc-quote</c>，签名块据此插到引用之前（与编辑器里的观感一致）。
/// </summary>
public static partial class MailQuoteBuilder
{
    /// <summary>引用块的容器 class（与 <see cref="ComposeBodyBuilder.QuoteClass"/> 一致，签名定位依赖它）。</summary>
    public const string QuoteClass = ComposeBodyBuilder.QuoteClass;

    /// <summary>引用块顶部的「原始邮件」抬头（发件人 / 时间 / 收件人 / 主题）。</summary>
    public const string HeadClass = "mc-quote-head";

    /// <summary>被引用正文的容器。</summary>
    public const string BodyClass = "mc-quote-body";

    /// <summary>引用块前面留一行空段落，光标默认停在这里开始写。</summary>
    public const string EmptyLineHtml = "<p><br/></p>";

    /// <summary>单张内嵌图片上限：超过就不内联，避免把几十兆图片塞进编辑器、草稿与发件队列。</summary>
    public const int MaxInlineImageBytes = 4 * 1024 * 1024;

    /// <summary>一次引用里内联图片的总量上限。</summary>
    public const int MaxTotalInlineImageBytes = 12 * 1024 * 1024;

    /// <summary>
    /// 生成引用块 HTML。
    /// </summary>
    /// <param name="detail">被回复 / 转发的邮件。</param>
    /// <param name="readLocalFile">读取 <c>file:///</c> 图片字节的委托（由调用方做目录白名单与异常兜底）；为空则不做内联。</param>
    public static string Build(EmailDetail detail, Func<string, byte[]?>? readLocalFile = null)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var body = string.IsNullOrWhiteSpace(detail.BodyHtml)
            ? HtmlTextExtractor.ToHtml(detail.BodyText ?? string.Empty)
            : HtmlMailDocumentBuilder.BuildEditorFragment(detail.BodyHtml);

        if (readLocalFile is not null) body = InlineLocalImages(body, readLocalFile);

        var sb = new StringBuilder(body.Length + 512);
        sb.Append(EmptyLineHtml);
        sb.Append("<div class=\"").Append(QuoteClass).Append("\">");
        sb.Append("<div class=\"").Append(HeadClass).Append("\">");
        sb.Append("<div>").Append(ComposeBodyBuilder.QuoteSeparator).Append("</div>");

        var from = string.IsNullOrWhiteSpace(detail.FromName)
            ? detail.FromEmail
            : $"{detail.FromName} <{detail.FromEmail}>";
        AppendRow(sb, "发件人：", from);
        AppendRow(sb, "发送时间：", detail.FullDateDisplay);
        AppendRow(sb, "收件人：", detail.ToDisplay);
        if (detail.Cc.Count > 0) AppendRow(sb, "抄送：", detail.CcDisplay);
        AppendRow(sb, "主题：", detail.SubjectDisplay);

        sb.Append("</div>");
        sb.Append("<div class=\"").Append(BodyClass).Append("\">").Append(body).Append("</div>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string label, string value)
    {
        sb.Append("<div><b>").Append(WebUtility.HtmlEncode(label)).Append("</b>")
            .Append(WebUtility.HtmlEncode(value ?? string.Empty))
            .Append("</div>");
    }

    /// <summary>
    /// 把 <c>&lt;img src="file:///…"&gt;</c> 就地内联为 data: URI。
    /// 超限、读不到、非图片扩展名的保持原样（渲染为空白，不会中断整段引用）。
    /// </summary>
    private static string InlineLocalImages(string html, Func<string, byte[]?> readLocalFile)
    {
        if (html.IndexOf("file:", StringComparison.OrdinalIgnoreCase) < 0) return html;

        var total = 0;
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return LocalImageRegex().Replace(html, match =>
        {
            var uri = match.Groups["uri"].Value;
            if (cache.TryGetValue(uri, out var cached)) return ReplaceSource(match, cached);
            if (total >= MaxTotalInlineImageBytes) return match.Value;

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return match.Value;

            byte[]? bytes;
            try
            {
                bytes = readLocalFile(parsed.LocalPath);
            }
            catch (Exception)
            {
                return match.Value; // 文件被删 / 无权限：保留原引用
            }

            if (bytes is null || bytes.Length == 0 || bytes.Length > MaxInlineImageBytes) return match.Value;

            var mediaType = MediaTypeOf(bytes, parsed.AbsolutePath);
            if (mediaType is null) return match.Value;

            total += bytes.Length;
            var dataUri = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
            cache[uri] = dataUri;
            return ReplaceSource(match, dataUri);
        });
    }

    /// <summary>
    /// 只替换匹配片段里的 URI 本身。注意匹配值以 <c>&lt;img … src="</c> 开头，
    /// 直接返回 <c>src="…"</c> 会把 <c>&lt;img</c> 标签头吃掉，data: 就变成正文里的一大段 base64 文字。
    /// </summary>
    private static string ReplaceSource(Match match, string dataUri)
    {
        var uri = match.Groups["uri"];
        var start = uri.Index - match.Index;
        return string.Concat(
            match.Value[..start],
            dataUri,
            match.Value[(start + uri.Length)..]);
    }

    /// <summary>按文件头识别图片类型（扩展名不可靠：不少邮件的内嵌图没有后缀）；识别不出返回 null。</summary>
    private static string? MediaTypeOf(byte[] bytes, string path)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        if (bytes.Length >= 6 &&
            bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
            return "image/gif";

        if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
            return "image/bmp";

        if (bytes.Length >= 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return "image/webp";

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => null
        };
    }

    [GeneratedRegex(@"<img\b[^>]*?\bsrc\s*=\s*(?<q>[""'])(?<uri>file:[^""']+)\k<q>",
        RegexOptions.IgnoreCase)]
    private static partial Regex LocalImageRegex();
}
