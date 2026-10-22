using System.Text;
using System.Net;
using System.Text.RegularExpressions;

namespace ZungMail.Core.Services;

/// <summary>正文中以 data: URI 内嵌的图片（发送前转换为 cid 内嵌资源）。</summary>
public sealed record InlineImage(string ContentId, string FileName, string MediaType, byte[] Data);

/// <summary>
/// 撰写正文的纯逻辑处理：纯文本 / HTML 判定、签名追加、内嵌图片提取。
/// 与 UI 无关，便于单元测试；发送链路据此把编辑器里的 data: 图片改写为 cid。
/// </summary>
public static partial class ComposeBodyBuilder
{
    /// <summary>单张内嵌图片的大小上限，避免把过大的图片写入邮件与队列。</summary>
    public const int MaxInlineImageBytes = 20 * 1024 * 1024;

    /// <summary>回复 / 转发时引用原文的分隔线（与 MainViewModel.BuildQuote 的输出保持一致）。</summary>
    public const string QuoteSeparator = "------------------ 原始邮件 ------------------";

    /// <summary>引用原文的外层容器 class：签名块据此插到引用之前，而不是埋到引用末尾。</summary>
    public const string QuoteClass = "mc-quote";

    /// <summary>
    /// 把撰写内容转换为编辑器可承载的 HTML；纯文本按行转段落。
    /// 回复 / 转发的引用原文会被包进 <c>.mc-quote</c>，这样签名块能插在它前面。
    /// </summary>
    public static string ToEditorHtml(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        if (LooksLikeHtml(body)) return body;

        var text = body.Replace("\r\n", "\n").Replace('\r', '\n');
        var separator = text.IndexOf(QuoteSeparator, StringComparison.Ordinal);
        if (separator < 0) return HtmlTextExtractor.ToHtml(text);

        // 分隔线之前是用户新写的内容（回复通常为空），之后是引用原文
        var head = text[..separator].TrimEnd('\n');
        var quote = text[separator..].TrimEnd('\n');

        var builder = new StringBuilder();
        if (head.Trim().Length > 0) builder.Append(HtmlTextExtractor.ToHtml(head));
        builder.Append("<div class=\"").Append(QuoteClass).Append("\">")
            .Append(HtmlTextExtractor.ToHtml(quote))
            .Append("</div>");
        return builder.ToString();
    }

    /// <summary>判断字符串是否为 HTML（仅识别常见标签，避免把「显示名 &lt;地址&gt;」误判）。</summary>
    public static bool LooksLikeHtml(string? text)
        => !string.IsNullOrEmpty(text) && HtmlTagRegex().IsMatch(text);

    /// <summary>
    /// 追加纯文本签名（旧版全局签名的兼容实现，保留给数据迁移与回归测试）。
    /// 正文（去标签后）已含同一签名时不重复追加，与旧实现保持一致的「同签名不叠加」行为。
    /// 新代码请用 <see cref="AppendSignatureHtml"/>，按账户的签名是富文本。
    /// </summary>
    public static string AppendSignature(string? bodyHtml, string? signature)
    {
        var body = bodyHtml ?? string.Empty;
        if (string.IsNullOrWhiteSpace(signature)) return body;

        var plain = HtmlTextExtractor.ToPlainText(body);
        if (plain.Contains(signature, StringComparison.Ordinal)) return body;

        var encoded = WebUtility.HtmlEncode(signature.Replace("\r\n", "\n").TrimEnd())
            .Replace("\n", "<br/>");
        return body + "<br/><br/>--<br/>" + encoded;
    }

    /// <summary>
    /// 追加富文本签名（签名本身是 HTML，可带加粗 / 颜色 / 图片等）。
    /// 正文（去标签后）已含同一签名时不重复追加，行为与纯文本版本一致。
    /// 回复 / 转发正文带 <c>.mc-quote</c> 引用块时，签名插在引用**之前**（与编辑器里看到的位置一致）。
    /// </summary>
    public static string AppendSignatureHtml(string? bodyHtml, string? signatureHtml)
    {
        var body = bodyHtml ?? string.Empty;
        if (string.IsNullOrWhiteSpace(signatureHtml)) return body;

        var signaturePlain = HtmlTextExtractor.ToPlainText(signatureHtml);
        if (string.IsNullOrWhiteSpace(signaturePlain))
        {
            // 纯图片签名没有文字可比对，改为比对图片地址：正文里已经有同一张图就不再追加
            var sources = ImageSourceRegex().Matches(signatureHtml)
                .Select(m => m.Groups["uri"].Value)
                .Where(uri => uri.Length > 0)
                .ToList();

            if (sources.Count == 0) return body;
            return sources.All(uri => body.Contains(uri, StringComparison.Ordinal))
                ? body
                : InsertSignature(body, signatureHtml);
        }

        if (HtmlTextExtractor.ToPlainText(body).Contains(signaturePlain, StringComparison.Ordinal)) return body;

        return InsertSignature(body, signatureHtml);
    }

    /// <summary>有引用块就插在引用前面，否则追加到末尾。</summary>
    private static string InsertSignature(string body, string signatureHtml)
    {
        var block = "<br/><br/>" + signatureHtml.Trim();
        var quoteOpen = FindQuoteBlockStart(body);
        return quoteOpen < 0 ? body + block : body.Insert(quoteOpen, block);
    }

    /// <summary>定位 <c>.mc-quote</c> 容器的起始标签位置；没有则返回 -1。</summary>
    private static int FindQuoteBlockStart(string html)
    {
        var marker = html.IndexOf(QuoteClass, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return -1;

        var open = html.LastIndexOf('<', marker);
        return open < 0 ? -1 : open;
    }

    /// <summary>
    /// 把正文中的 data:image 图片改写为 cid: 引用，并返回待作为内嵌资源发送的图片。
    /// 相同 data URI 只生成一个 cid，保持邮件体积不因重复粘贴而翻倍。
    /// </summary>
    public static (string Html, IReadOnlyList<InlineImage> Images) ExtractInlineImages(string? html)
    {
        var source = html ?? string.Empty;
        if (source.IndexOf("data:image", StringComparison.OrdinalIgnoreCase) < 0)
            return (source, Array.Empty<InlineImage>());

        var images = new List<InlineImage>();
        var byUri = new Dictionary<string, InlineImage>(StringComparer.Ordinal);

        var result = DataImageRegex().Replace(source, match =>
        {
            var quote = match.Groups["q"].Value;
            var uri = match.Groups["uri"].Value;
            if (byUri.TryGetValue(uri, out var existing))
                return $"src={quote}cid:{existing.ContentId}{quote}";

            var payload = match.Groups["data"].Value;
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                return match.Value; // 非法 base64：保留原样，避免破坏正文
            }

            if (bytes.Length == 0 || bytes.Length > MaxInlineImageBytes) return match.Value;

            var mediaType = NormalizeMediaType(match.Groups["type"].Value);
            var image = new InlineImage(
                $"{Guid.NewGuid():N}@zungmail.local",
                "image" + ExtensionOf(mediaType),
                mediaType,
                bytes);

            byUri[uri] = image;
            images.Add(image);
            return $"src={quote}cid:{image.ContentId}{quote}";
        });

        return (result, images);
    }

    private static string NormalizeMediaType(string mediaType)
    {
        var type = mediaType.Trim().ToLowerInvariant();
        return type switch
        {
            "image/jpg" => "image/jpeg",
            "image/svg+xml" => "image/svg+xml",
            _ => type
        };
    }

    private static string ExtensionOf(string mediaType) => mediaType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/svg+xml" => ".svg",
        _ => ".img"
    };

    [GeneratedRegex(@"<img\b[^>]*?\bsrc\s*=\s*[""'](?<uri>[^""']+)[""']",
        RegexOptions.IgnoreCase)]
    private static partial Regex ImageSourceRegex();

    [GeneratedRegex(@"src\s*=\s*(?<q>[""'])(?<uri>data:(?<type>image/[a-z0-9.+-]+);base64,(?<data>[^""']+))\k<q>",
        RegexOptions.IgnoreCase)]
    private static partial Regex DataImageRegex();

    [GeneratedRegex(@"<(?:a|b|blockquote|br|div|em|font|h[1-6]|hr|i|img|li|ol|p|pre|span|strong|table|tbody|td|th|thead|tr|u|ul)\b(?=[\s/>])",
        RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagRegex();
}
