using System.Text.RegularExpressions;

namespace ZungMail.Core.Services;

/// <summary>
/// 正文里 base64 <c>data:image/…</c> 内嵌图的落盘与改写。
///
/// 货代邮件里常见「截图直接贴进正文」的写法（Outlook / Foxmail / 网页邮箱都这么干）：
/// 一张 100KB 的图 base64 编码后约 136KB，一封带 57 张图的邮件正文就是 6.8MB。
/// 这些字节留在 <c>body_html</c> 里会同时拖累三处：库里存 6.8MB、每次打开读 6.8MB、
/// 渲染时整篇文档塞给 WebView2。改成「落盘 + 改写成 file:///」后，
/// 库里只剩引用了，渲染走已有的虚拟主机映射按需加载。
///
/// 只处理 base64 形式的图片（<c>data:image/png;base64,…</c>）；
/// 非 base64 的 data:（<c>data:image/svg+xml;utf8,</c>）、非图片类型（字体 / CSS）一律原样保留。
/// </summary>
public static partial class MailBodyDataImageExtractor
{
    /// <summary>单张图片解码后的上限：超过就保持原样（宁可大文档，也不把异常数据写到磁盘）。</summary>
    public const int MaxImageBytes = 6 * 1024 * 1024;

    /// <summary>一封邮件里所有内嵌图的上限：超过之后剩下的保持原样。</summary>
    public const long MaxTotalBytes = 24 * 1024 * 1024;

    /// <summary>落盘回调：把字节写成文件并返回要替换进正文的 <c>file:///…</c> 地址；返回 null 表示放弃替换。</summary>
    public delegate string? DataImageSink(string mimeType, byte[] bytes, int index);

    /// <summary>正文里是否还有 base64 图片（做一次廉价判断，避免对普通正文跑整趟正则）。</summary>
    public static bool HasDataImages(string? html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        var at = html.IndexOf("data:image/", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        return html.IndexOf("base64", at, StringComparison.OrdinalIgnoreCase) > at;
    }

    /// <summary>
    /// 把正文里的 base64 图片交给 <paramref name="sink"/> 落盘，并把对应的 data: 片段替换成返回的地址。
    /// 没有 data: 图片时原样返回（不复制字符串）。
    /// </summary>
    public static string ReplaceDataImages(string? html, DataImageSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        if (!HasDataImages(html)) return html ?? string.Empty;

        var totalBytes = 0L;
        var index = 0;

        return DataImageRegex().Replace(html!, match =>
        {
            var mime = match.Groups["mime"].Value.ToLowerInvariant();
            if (!ExtensionByMime.TryGetValue(mime, out _)) return match.Value;

            var payload = match.Groups["data"].Value;

            // 先按长度挡一层：一行 40MB 的 base64 不该为了判断超限就解码出来
            if (payload.Length > MaxImageBytes * 2) return match.Value;

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                // 换行/换行符之外还有非法字符：保持原样，渲染端仍能显示
                return match.Value;
            }

            if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return match.Value;
            if (totalBytes + bytes.Length > MaxTotalBytes) return match.Value;

            index++;
            var uri = sink(mime, bytes, index);
            if (string.IsNullOrEmpty(uri)) return match.Value;

            totalBytes += bytes.Length;
            return uri;
        });
    }

    /// <summary>按 mime 子类型给出文件扩展名；不在表里的（少见格式）不落盘。</summary>
    public static string? ExtensionFor(string mimeType) => ExtensionByMime.TryGetValue(mimeType, out var ext) ? ext : null;

    private static readonly Dictionary<string, string> ExtensionByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/x-png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/pjpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/bmp"] = ".bmp",
        ["image/x-ms-bmp"] = ".bmp",
        ["image/tiff"] = ".tiff",
        ["image/avif"] = ".avif",
        ["image/heic"] = ".heic"
    };

    /// <summary>
    /// 匹配 <c>data:image/png;base64,AAAA…</c>。
    /// 折行的 base64（有些客户端按 76 列折）靠 <c>(?:\s*[A-Za-z0-9+/=]+)*</c> 续行：
    /// 空白只有在「后面还跟着 base64 字符」时才吃进来，末尾的空白不会被吞，
    /// 否则替换后正文里图片和后面的文字会粘在一起。
    /// </summary>
    [GeneratedRegex(
        @"data:(?<mime>image/[a-z0-9.+-]+)\s*;\s*base64\s*,(?<data>[A-Za-z0-9+/=]+(?:\s*[A-Za-z0-9+/=]+)*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DataImageRegex();
}
