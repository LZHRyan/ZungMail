using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>HTML 正文渲染选项。</summary>
public sealed record MailDocumentOptions
{
    /// <summary>是否暗色主题。</summary>
    public bool IsDark { get; init; }

    /// <summary>正文字号（px）。</summary>
    public double FontSize { get; init; } = 13.5;

    /// <summary>是否允许加载远程图片（false 时仅允许内嵌图片与 data:）。</summary>
    public bool AllowRemoteImages { get; init; }

    /// <summary>附件根目录；用于把 file:/// 引用改写到虚拟主机。</summary>
    public string? EmlDirectory { get; init; }

    /// <summary>承载附件的虚拟主机名（需与 WebView2 的映射保持一致）。</summary>
    public string VirtualHost { get; init; } = HtmlMailDocumentBuilder.DefaultVirtualHost;

    /// <summary>多层引用的历史邮件是否默认折叠（设置里「默认折叠超长邮件的历史引用」）。</summary>
    public bool FoldQuotedHistory { get; init; } = true;

    /// <summary>折叠时保留最近几层引用直接显示，更早的才收起（5–10，0 表示整段收起）。</summary>
    public int FoldQuotedHistoryKeepLayers { get; init; } = MailQuoteFolder.DefaultKeepVisibleLayers;
}

/// <summary>
/// 把邮件 HTML 片段组装为可安全渲染的完整文档：剥离脚本类元素、注入 CSP 与阅读样式、
/// 把本地 file:/// 图片改写到虚拟主机。纯逻辑、无 UI 依赖，便于单元测试。
/// </summary>
public static partial class HtmlMailDocumentBuilder
{
    /// <summary>附件目录映射的虚拟主机。</summary>
    public const string DefaultVirtualHost = "zungmailassets.example";

    /// <summary>用邮件详情组装文档；无 HTML 时回退为纯文本段落。</summary>
    public static string Build(EmailDetail detail, MailDocumentOptions options)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(options);

        var html = string.IsNullOrWhiteSpace(detail.BodyHtml)
            ? HtmlTextExtractor.ToHtml(detail.BodyText)
            : detail.BodyHtml;

        return Build(html, options);
    }

    /// <summary>用原始 HTML 组装文档。</summary>
    public static string Build(string? html, MailDocumentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var source = html ?? string.Empty;
        var headStyles = ExtractHeadStyles(source, options.IsDark);
        var fragment = ExtractBodyFragment(source);
        fragment = RemoveDangerousContent(fragment);
        fragment = RewriteLocalUris(fragment, options);
        // 引用的历史邮件里常有指向别的邮件的 cid: 图片，部件不在本封邮件里，渲染出来只是破图：
        // 阅读区换成一句「图片未同步」的提示，用户能看出这里原本有图
        fragment = ReplaceUnresolvedInlineImages(fragment, withPlaceholder: true);

        // 几十轮往返的长线程：深层引用压平缩进；折叠与否、保留最近几层都跟随设置
        fragment = MailQuoteFolder.Rewrite(
            fragment,
            fold: options.FoldQuotedHistory,
            keepVisibleLayers: options.FoldQuotedHistoryKeepLayers).Html;

        var sb = new StringBuilder(fragment.Length + headStyles.Length + 2048);
        sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"");
        sb.Append(BuildCsp(options));
        sb.Append("\"><meta name=\"referrer\" content=\"no-referrer\">");
        // 邮件自带的 <style> 放在阅读器基础样式之前：表格边框、border-collapse 等排版规则得以保留，
        // 而字号、字体、前景色等阅读偏好仍由后出现的基础样式统一覆盖。
        if (headStyles.Length > 0) sb.Append("<style>").Append(headStyles).Append("</style>");
        sb.Append("<style>").Append(BuildCss(options)).Append("</style>");
        sb.Append("</head><body><div id=\"mail-root\">");
        sb.Append(fragment);
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>判断 HTML 是否引用远程图片（http/https 或协议相对地址）。</summary>
    public static bool ContainsRemoteImages(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;
        return RemoteImageRegex().IsMatch(html) || RemoteCssUrlRegex().IsMatch(html);
    }

    /// <summary>
    /// 把邮件正文清洗成可嵌进编辑器的片段：去掉脚本 / 框架 / 表单刷新等危险内容，
    /// 并丢掉正文自带的 &lt;style&gt;（整篇样式会污染撰写页），保留内联样式带来的排版。
    /// 多层引用沿用阅读器的压平规则，但不折叠——撰写时用户要能看到并删改被引用的内容。
    /// </summary>
    public static string BuildEditorFragment(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var fragment = ExtractBodyFragment(html);
        fragment = RemoveDangerousContent(fragment);
        fragment = StyleElementRegex().Replace(fragment, " ");
        // 引用历史里的 cid: 图片同样无法解析；撰写区直接丢掉（占位文字会被一起发出去）
        fragment = ReplaceUnresolvedInlineImages(fragment, withPlaceholder: false);
        return MailQuoteFolder.Rewrite(fragment, fold: false).Html;
    }

    // ---------------- 文档片段 ----------------

    private static string ExtractBodyFragment(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var body = BodyRegex().Match(html);
        if (body.Success) return body.Groups[1].Value.Trim();

        var fragment = HeadRegex().Replace(html, " ");
        fragment = DoctypeRegex().Replace(fragment, " ");
        fragment = HtmlTagRegex().Replace(fragment, " ");
        return fragment.Trim();
    }

    /// <summary>
    /// 提取 &lt;head&gt; 中的 &lt;style&gt; 内容。邮件常把 table/th/td 的边框与 border-collapse
    /// 写在这里，只取 body 片段会让表格线框整体丢失。正文内的 &lt;style&gt; 仍随片段保留。
    /// </summary>
    private static string ExtractHeadStyles(string html, bool isDark)
    {
        if (html.IndexOf("<style", StringComparison.OrdinalIgnoreCase) < 0) return string.Empty;

        int end;
        var bodyIndex = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyIndex >= 0)
        {
            end = bodyIndex;
        }
        else
        {
            var head = HeadRegex().Match(html);
            if (!head.Success) return string.Empty; // 无 <head> 的片段：样式留在正文中按原位置生效
            end = head.Index + head.Length;
        }

        var css = new StringBuilder();
        foreach (Match match in StyleElementRegex().Matches(html[..end]))
        {
            var content = SanitizeCss(match.Groups[1].Value);
            // 暗色主题下丢弃 head 样式里的前景色/背景色：这类声明多按浅色底设计，
            // 保留会在深色底上留下浅色块与不可读文字；边框、宽高、内边距等排版规则仍完整保留。
            if (isDark) content = ThemeColorRegex().Replace(content, " ");
            if (content.Length == 0) continue;
            css.Append(content).Append('\n');
        }

        return css.ToString();
    }

    /// <summary>移除外部 @import 与可执行样式扩展，避免绕过 CSP 或触发非预期加载。</summary>
    private static string SanitizeCss(string css)
    {
        var s = CssImportRegex().Replace(css, " ");
        s = CssDangerousRegex().Replace(s, " ");
        return s.Trim();
    }

    /// <summary>剥离脚本、框架、表单刷新等可执行/可外联元素与内联事件处理器。</summary>
    private static string RemoveDangerousContent(string html)
    {
        var s = ScriptRegex().Replace(html, " ");
        s = IframeRegex().Replace(s, " ");
        s = ObjectRegex().Replace(s, " ");
        s = EmbedRegex().Replace(s, " ");
        s = BaseRegex().Replace(s, " ");
        s = LinkRegex().Replace(s, " ");
        s = MetaRefreshRegex().Replace(s, " ");
        s = InlineEventRegex().Replace(s, " ");
        s = JavaScriptHrefRegex().Replace(s, "href=\"#\"");
        // 正文内的 <style> 同样去掉 @import 等外联入口（CSP 已禁止外链样式，这里避免残留请求尝试）
        s = StyleElementRegex().Replace(s, m => "<style>" + SanitizeCss(m.Groups[1].Value) + "</style>");
        return s;
    }

    /// <summary>把指向附件目录的 file:/// 引用改写为虚拟主机地址。</summary>
    private static string RewriteLocalUris(string html, MailDocumentOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.EmlDirectory)) return html;
        if (html.IndexOf("file://", StringComparison.OrdinalIgnoreCase) < 0) return html;

        string root;
        try
        {
            root = Path.GetFullPath(options.EmlDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return html; // 路径非法时保持原样，交给 CSP 兜底
        }

        var prefix = root + Path.DirectorySeparatorChar;

        return FileUriRegex().Replace(html, match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) || !uri.IsFile)
                return match.Value;

            string localPath;
            try
            {
                localPath = Path.GetFullPath(uri.LocalPath);
            }
            catch (Exception)
            {
                return match.Value;
            }

            if (!localPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return match.Value;

            var relative = Path.GetRelativePath(root, localPath).Replace('\\', '/');
            var encoded = string.Join("/", relative.Split('/').Select(Uri.EscapeDataString));
            return $"https://{options.VirtualHost}/{encoded}";
        });
    }

    /// <summary>
    /// 处理无法解析的 <c>cid:</c> 内嵌图片。
    ///
    /// 导入邮件时 <c>MimeMapper</c> 已经把这封邮件自己的内嵌图片从 cid: 改写成本地文件，
    /// 所以正文里还残留的 cid: 一律来自「引用的历史邮件」——图片部件属于更早的那封邮件，
    /// 不在这封邮件的 MIME 里，浏览器只能画出破图（或按 alt 撑出一块空白）。
    /// 超长线程折叠区一展开就是一串破图：阅读区换成一句「图片未同步」的提示，
    /// 撰写区则直接删掉（提示文字不能被一起发出去）。
    /// </summary>
    private static string ReplaceUnresolvedInlineImages(string html, bool withPlaceholder)
    {
        if (html.IndexOf("cid:", StringComparison.OrdinalIgnoreCase) < 0) return html;

        var parser = new HtmlParser();
        var document = parser.ParseDocument("<!DOCTYPE html><html><body>" + html + "</body></html>");
        var body = document.Body;
        if (body is null) return html;

        var images = body.QuerySelectorAll("img")
            .OfType<IElement>()
            .Where(image => (image.GetAttribute("src") ?? string.Empty)
                .TrimStart()
                .StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (images.Count == 0) return html;

        foreach (var image in images)
        {
            if (!withPlaceholder)
            {
                image.Remove();
                continue;
            }

            // 单个「图」字小方块：签名栏的表格单元格很窄，长文案会竖着折行把版面撑坏
            var placeholder = document.CreateElement("span");
            placeholder.ClassName = "mc-missing-image";
            placeholder.SetAttribute("title", "图片未随这封邮件转发，无法显示");
            placeholder.TextContent = "图";
            image.Replace(placeholder);
        }

        return body.InnerHtml;
    }

    // ---------------- 安全策略与样式 ----------------
    private static string BuildCsp(MailDocumentOptions options)
    {
        var mediaSources = options.AllowRemoteImages
            ? $"https://{options.VirtualHost} data: https: http:"
            : $"https://{options.VirtualHost} data:";

        var imageSources = options.AllowRemoteImages
            ? $"https://{options.VirtualHost} data: https: http:"
            : $"https://{options.VirtualHost} data:";

        return string.Join("; ",
            "default-src 'none'",
            $"img-src {imageSources}",
            $"media-src {mediaSources}",
            "style-src 'unsafe-inline'",
            "font-src 'none'",
            "script-src 'none'",
            "frame-src 'none'",
            "object-src 'none'",
            "connect-src 'none'",
            "base-uri 'none'",
            "form-action 'none'");
    }

    private static string BuildCss(MailDocumentOptions options)
    {
        var bg = options.IsDark ? "#161B23" : "#FFFFFF";
        var ink = options.IsDark ? "#E9EEF7" : "#131922";
        var muted = options.IsDark ? "#7B8AA1" : "#7D889C";
        var rule = options.IsDark ? "#262F3C" : "#E6EAF2";
        var accent = options.IsDark ? "#5B8BFF" : "#2563EB";
        var size = options.FontSize > 0 ? options.FontSize : 13.5;

        var css = new StringBuilder();
        css.Append(":root{")
            .Append("--bg:").Append(bg).Append(';')
            .Append("--ink:").Append(ink).Append(';')
            .Append("--muted:").Append(muted).Append(';')
            .Append("--rule:").Append(rule).Append(';')
            .Append("--accent:").Append(accent).Append(';')
            .Append("--font-size:").Append(size.ToString("0.#", CultureInfo.InvariantCulture)).Append("px;")
            .Append('}');
        css.Append("html,body{margin:0;padding:0;background:var(--bg);color:var(--ink);}");
        css.Append("body{font-family:'Segoe UI','Microsoft YaHei UI','Microsoft YaHei',system-ui,-apple-system,'Noto Sans CJK SC',sans-serif;");
        css.Append("font-size:var(--font-size);line-height:1.7;-webkit-text-size-adjust:100%;}");
        css.Append("#mail-root{max-width:100%;padding:2px 22px 28px 22px;box-sizing:border-box;overflow-wrap:anywhere;}");
        // 邮件自带样式可能禁用选中；阅读器始终允许选中正文并复制
        css.Append("#mail-root,#mail-root *{-webkit-user-select:text !important;user-select:text !important;}");
        css.Append("img{max-width:100% !important;height:auto !important;}");
        // 内嵌图片没随邮件传过来时的占位：18px 小方块，任何窄表格单元格里都不会折行
        css.Append("#mail-root .mc-missing-image{display:inline-block;width:18px;height:18px;");
        css.Append("line-height:16px;text-align:center;padding:0;margin:0 2px;white-space:nowrap;");
        css.Append("border:1px dashed var(--rule);border-radius:4px;color:var(--muted);");
        css.Append("font-size:11px;vertical-align:middle;user-select:none;}");
        css.Append("table{max-width:100% !important;}");
        // 表格边框规范化（:where 为零优先级，邮件自带样式与内联样式仍可覆盖）：
        // Outlook/Word 按合并边框渲染 border 属性表格，Chromium 默认独立边框会把相邻单元格
        // 各画的 1px 叠成 2px 粗线；cellspacing="0" 的表格也会出现同样的双线。
        css.Append(":where(table[border]:not([border=\"0\"]):not([border=\"0px\"])){border-collapse:collapse;}");
        css.Append(":where(table[cellspacing=\"0\"]){border-collapse:collapse;}");
        css.Append("pre{white-space:pre-wrap;word-break:break-word;}");
        css.Append("a{color:var(--accent);}");
        css.Append("blockquote{margin:0 0 0 4px;padding-left:12px;border-left:2px solid var(--rule);color:var(--muted);}");
        // 第 3 层起彻底不再缩进：宽度不随层数变化，层级感交给「变淡 + 上一层的引用条」。
        // 除了 blockquote 本身，真实邮件每层还包着带 margin/padding/border 的 div，
        // 这里一并清零（只清横向，纵向间距与表格布局保持原样），否则 39 层仍会挤成零宽。
        css.Append("#mail-root .q-deep{margin-left:0 !important;margin-right:0 !important;");
        css.Append("padding-left:0 !important;padding-right:0 !important;");
        css.Append("border-left:0 !important;border-right:0 !important;text-indent:0 !important;}");
        // 折叠的历史引用：原生 details/summary，收起时浏览器不布局这棵子树
        css.Append("#mail-root details.quote-fold{margin:8px 0 0 0;padding:0;}");
        css.Append("#mail-root details.quote-fold>summary{cursor:pointer;color:var(--accent);font-size:12.5px;line-height:1.6;");
        css.Append("padding:2px 0 4px 0;user-select:none;list-style:none;display:block;}");
        css.Append("#mail-root details.quote-fold>summary::-webkit-details-marker{display:none;}");
        css.Append("#mail-root details.quote-fold>summary::before{content:\"▸\";display:inline-block;width:14px;}");
        css.Append("#mail-root details.quote-fold[open]>summary::before{content:\"▾\";}");
        // 长正文：视口外的顶层块跳过布局与绘制（Chromium 支持），首屏只排版可见部分
        css.Append("#mail-root>*{content-visibility:auto;contain-intrinsic-size:auto 600px;}");
        css.Append("hr{border:0;border-top:1px solid var(--rule);}");
        css.Append(".mail-empty{color:var(--muted);}");
        // 正文滚动条：Chromium 默认约 15px，和 WPF 侧的细滚动条差异明显，这里统一收窄到 6px
        css.Append("::-webkit-scrollbar{width:6px;height:6px;}");
        css.Append("::-webkit-scrollbar-track{background:transparent;}");
        css.Append("::-webkit-scrollbar-thumb{background:rgba(100,116,139,.45);border-radius:3px;}");
        css.Append("::-webkit-scrollbar-thumb:hover{background:rgba(100,116,139,.75);}");
        return css.ToString();
    }

    // ---------------- 正则 ----------------

    [GeneratedRegex(@"<body\b[^>]*>([\s\S]*?)</body\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BodyRegex();

    [GeneratedRegex(@"<head\b[^>]*>[\s\S]*?</head\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadRegex();

    [GeneratedRegex(@"<!DOCTYPE[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DoctypeRegex();

    [GeneratedRegex(@"</?html\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"<script\b[^>]*>[\s\S]*?</script\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<iframe\b[^>]*>[\s\S]*?</iframe\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex IframeRegex();

    [GeneratedRegex(@"<object\b[^>]*>[\s\S]*?</object\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ObjectRegex();

    [GeneratedRegex(@"<embed\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex EmbedRegex();

    [GeneratedRegex(@"<base\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BaseRegex();

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"<style\b[^>]*>([\s\S]*?)</style\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleElementRegex();

    [GeneratedRegex(@"@import\s+(?:url\s*\([^)]*\)|[^;]*);?", RegexOptions.IgnoreCase)]
    private static partial Regex CssImportRegex();

    [GeneratedRegex(@"(?:expression\s*\(|javascript\s*:|vbscript\s*:|behavior\s*:|-moz-binding\s*:)", RegexOptions.IgnoreCase)]
    private static partial Regex CssDangerousRegex();

    /// <summary>匹配前景色与背景色声明（不含 border-color 等复合属性）。</summary>
    [GeneratedRegex(@"(?<![-\w])(?:background(?:-[a-z]+)*|color)\s*:[^;}]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex ThemeColorRegex();

    [GeneratedRegex(@"<meta\b[^>]*http-equiv\s*=\s*[""']?refresh[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefreshRegex();

    [GeneratedRegex(@"\son[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineEventRegex();

    [GeneratedRegex(@"href\s*=\s*(?:""\s*javascript:[^""]*""|'\s*javascript:[^']*'|javascript:[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex JavaScriptHrefRegex();

    [GeneratedRegex(@"file:///[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex FileUriRegex();

    [GeneratedRegex(@"<[a-z][^>]*\b(?:src|poster|background)\s*=\s*[""']?\s*(?:https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteImageRegex();

    [GeneratedRegex(@"url\(\s*[""']?\s*(?:https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteCssUrlRegex();
}
