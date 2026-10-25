using System.Text;

namespace ZungMail.Core.Services;

/// <summary>
/// 正文渲染形态的判定口径：一本正经的大邮件该走哪条路，在哪儿必须止损。
///
/// 两条硬约束决定了这里的数字：
/// 1. <c>CoreWebView2.NavigateToString</c> 的实参上限是 2MB（WebView2 官方文档口径），
///    超了直接抛「Value does not fall within the expected range」——8MB 的邮件永远走不通，
///    以前这条异常被静默吞掉，然后落到降级位面，界面一冻就是几秒；
/// 2. WPF 的 FlowDocument 是逐元素构建的，几 MB 的 HTML 要一两秒（实测 6.8MB ≈ 1.7–3.1s），
///    所以降级位面只对「小正文」开富文本，大正文直接排纯文本。
/// </summary>
public static class MailBodyRenderPolicy
{
    /// <summary>
    /// 文档字节数上限：NavigateToString 的 2MB 硬限制之下留 25% 余量，
    /// 免得中文邮件按 UTF-8 三字节/字算超。
    /// </summary>
    public const int NavigateToStringByteBudget = 1_500_000;

    /// <summary>富文本降级（WPF FlowDocument）能接受的 HTML 体积上限（字符数）。</summary>
    public const int RichFallbackMaxHtmlChars = 512 * 1024;

    /// <summary>大正文按纯文本降级时顶部的说明，避免用户以为排版丢了。</summary>
    public const string LargeBodyFallbackNotice =
        "（这封邮件的正文很大，已按纯文本显示：HTML 排版与内嵌图片已省略，内容与上面一致。）";

    /// <summary>组装好的文档是否超过 <c>NavigateToString</c> 的安全体积，需要写文件走虚拟主机导航。</summary>
    public static bool NeedsFileNavigation(string? document)
    {
        if (string.IsNullOrEmpty(document)) return false;

        // 必须按 UTF-8 字节算：中文邮件一个字三字节，按字符数判断会漏（2MB 的额度只用掉三分之一）
        return Encoding.UTF8.GetByteCount(document) > NavigateToStringByteBudget;
    }

    /// <summary>这封邮件的 HTML 是否还值得交给 WPF 富文本降级位面。</summary>
    public static bool AllowRichFallback(string? html)
        => html is null || html.Length <= RichFallbackMaxHtmlChars;
}
