using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace ZungMail.Core.Services;

/// <summary>长邮件正文里「引用一层层缩进」的处理结果。</summary>
public sealed record QuotedHistoryRewrite(string Html, int MaxDepth, bool Folded);

/// <summary>
/// 处理几十轮往返的长线程：引用（blockquote）每嵌套一层就会吃掉一段左侧宽度，
/// 越往下（越早）的正文被挤得越窄，行数暴增。
///
/// 这里做三件事：
/// 1. 深于 <see cref="DeepQuoteLevel"/> 层的引用打上 <c>q-deep</c>，CSS 里不再累加缩进；
///    光压 &lt;blockquote&gt; 不够——真实邮件（Outlook/Foxmail/网页邮箱）每层还会套一层
///    带 <c>margin:10px</c>、<c>padding-left:20px</c>、<c>border-left</c> 的 &lt;div&gt;，
///    39 层时会额外累积一千多像素，所以同一层级的所有容器元素一起压平（表格家族保留，避免破坏报价表边框）；
/// 2. 把最外层引用（连同其中所有层级）折叠进原生 <c>&lt;details&gt;</c>，默认收起——
///    收起状态浏览器不布局这棵子树，长线程的首屏成本直接降下来；
/// 3. 引用里的图片补 <c>loading="lazy"</c> / <c>decoding="async"</c>。
///
/// 只有「确实存在多层引用」的正文才会走 DOM 解析（<see cref="NeedsRewrite"/>），
/// 普通邮件保持原来的字符串路径，避免不必要的重排与格式变化。
/// </summary>
public static class MailQuoteFolder
{
    /// <summary>从第几层引用开始压平缩进（1、2 层保持原有层级感）。</summary>
    public const int DeepQuoteLevel = 3;

    /// <summary>默认保留最近几层引用直接显示，更早的才折起来（设置里可选 5–10）。</summary>
    public const int DefaultKeepVisibleLayers = 5;

    /// <summary>设置里「保留最近 N 层」的最小档位。</summary>
    public const int MinKeepVisibleLayers = 5;

    /// <summary>设置里「保留最近 N 层」的最大档位。</summary>
    public const int MaxKeepVisibleLayers = 10;

    /// <summary>折叠后 summary 上的文案模板；{0} 是被收起的那几层引用。</summary>
    public const string SummaryTemplate = "显示更早的历史邮件（{0} 层）";

    private const string DeepClass = "q-deep";
    private const string FoldClass = "quote-fold";

    /// <summary>是否值得走 DOM 改写：至少有 2 个 blockquote 才可能形成多层嵌套。</summary>
    public static bool NeedsRewrite(string? html)
    {
        if (string.IsNullOrEmpty(html)) return false;

        var count = 0;
        var index = 0;
        while (index < html.Length)
        {
            index = html.IndexOf("<blockquote", index, StringComparison.OrdinalIgnoreCase);
            if (index < 0) break;
            count++;
            if (count >= 2) return true;
            index += 11;
        }

        return false;
    }

    /// <summary>
    /// 改写正文片段。返回的新片段可直接放进阅读器容器；
    /// 未命中多层引用时原样返回，<see cref="QuotedHistoryRewrite.MaxDepth"/> 为实际层数。
    /// </summary>
    /// <param name="fold">是否折叠更早的历史引用。回复 / 转发引用原文时不折叠（见 <see cref="MailQuoteBuilder"/>）。</param>
    /// <param name="keepVisibleLayers">
    /// 保留最近几层引用直接显示，只把更早的折进 &lt;details&gt;；
    /// 0 表示整段历史一次性收起（旧行为）。层数不够时整篇保持展开。
    /// </param>
    public static QuotedHistoryRewrite Rewrite(
        string? html,
        bool fold = true,
        int keepVisibleLayers = DefaultKeepVisibleLayers)
    {
        var source = html ?? string.Empty;
        if (!NeedsRewrite(source)) return new QuotedHistoryRewrite(source, 0, false);

        var parser = new HtmlParser();
        var document = parser.ParseDocument("<!DOCTYPE html><html><body>" + source + "</body></html>");
        var body = document.Body;
        if (body is null) return new QuotedHistoryRewrite(source, 0, false);

        var quotes = body.QuerySelectorAll("blockquote").OfType<IHtmlElement>().ToList();
        if (quotes.Count < 2) return new QuotedHistoryRewrite(source, 0, false);

        var maxDepth = 0;
        foreach (var quote in quotes)
        {
            var depth = Depth(quote);
            maxDepth = Math.Max(maxDepth, depth);
            if (depth >= DeepQuoteLevel) quote.ClassList.Add(DeepClass);
        }

        // 深层引用里的「套娃容器」也要压平：每层引用往往被一层带 margin/padding 的 div 包住，
        // 只压 blockquote 的话 39 层仍会累积出上千像素缩进（实测最低层文本列宽为 0）。
        foreach (var element in body.QuerySelectorAll("*"))
        {
            if (!ShouldFlatten(element)) continue;
            if (Depth(element) < DeepQuoteLevel) continue;
            element.ClassList.Add(DeepClass);
        }

        // 引用里的图片一律懒加载：收起的引用不会被解码
        foreach (var image in body.QuerySelectorAll("img"))
        {
            if (string.IsNullOrEmpty(image.GetAttribute("loading"))) image.SetAttribute("loading", "lazy");
            if (string.IsNullOrEmpty(image.GetAttribute("decoding"))) image.SetAttribute("decoding", "async");
        }

        var folded = false;
        var keep = Math.Clamp(keepVisibleLayers, 0, MaxKeepVisibleLayers);
        if (fold && maxDepth > keep)
        {
            // 只处理「保留层数 + 1」这一层的引用：它是最早的一层可见引用的直接上/下文，
            // 从这里往下整段收起来，最新的 keep 层仍然直接铺开，用户不用点开就能看最近几封。
            var foldDepth = keep + 1;
            var anchors = quotes.Where(q => Depth(q) == foldDepth).ToList();
            foreach (var quote in anchors)
            {
                var subtreeDepth = MaxDepthInside(quote);

                var details = document.CreateElement("details");
                details.ClassName = FoldClass;

                var summary = document.CreateElement("summary");
                summary.TextContent = string.Format(SummaryTemplate, subtreeDepth);
                details.AppendChild(summary);

                quote.Parent?.InsertBefore(details, quote);
                details.AppendChild(quote);
                folded = true;
            }
        }

        return new QuotedHistoryRewrite(body.InnerHtml, maxDepth, folded);
    }

    /// <summary>元素所在的引用层级（最外层为 1）。</summary>
    private static int Depth(IElement element)
    {
        var depth = 0;
        var current = element;
        while (current is not null)
        {
            if (string.Equals(current.LocalName, "blockquote", StringComparison.OrdinalIgnoreCase)) depth++;
            current = current.ParentElement;
        }

        return depth;
    }

    /// <summary>
    /// 表格家族不参与压平：<c>border</c>/<c>cellpadding</c> 画的是报价表的网格与留白，
    /// 清零会把表格内容挤在一起、丢掉分隔线，而表格本身宽度受 100% 约束，不会一路累积成窄条。
    /// 行内元素（span/a/b/img…）同样跳过：它们的 margin/padding 不形成块级缩进链。
    /// </summary>
    private static bool ShouldFlatten(IElement element)
    {
        var name = element.LocalName ?? string.Empty;
        if (!FlattenTargets.Contains(name)) return false;

        // 引用本身就带浏览器默认的 40px 左外边距，必须压
        if (name == "blockquote") return true;

        // 没有 style/class 的普通容器拿不到缩进来源，跳过可以少写大量 class（39 层长线程省三成体积）
        return element.HasAttribute("style") || element.HasAttribute("class");
    }

    private static readonly HashSet<string> FlattenTargets = new(StringComparer.Ordinal)
    {
        "blockquote", "div", "section", "article", "center", "p", "address", "figure",
        "ul", "ol", "li", "dl", "dd", "dt", "pre", "fieldset", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "footer", "main", "aside"
    };

    /// <summary>以某个引用为根的最大嵌套深度（它自身算 1）。</summary>
    private static int MaxDepthInside(IElement quote)
    {
        var deepest = 1;
        foreach (var child in quote.QuerySelectorAll("blockquote"))
        {
            deepest = Math.Max(deepest, Depth(child) - Depth(quote) + 1);
        }

        return deepest;
    }
}
