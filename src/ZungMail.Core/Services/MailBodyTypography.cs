namespace ZungMail.Core.Services;

/// <summary>
/// 正文渲染的字体口径：**只统一字族**，不改颜色也不改字号。
/// <para>
/// 邮件正文常带着 Word/Foxmail 留下的 <c>font-family:宋体</c>、<c>&lt;font face&gt;</c> 或
/// 中英混排的 <c>Arial + 宋体</c>，同一封信里出现好几种字族，读起来很乱。
/// 这里给出一份统一字体栈：WebView2 路径用它强制覆盖所有后代，WPF 降级位面用它设定整篇文档。
/// 颜色、字号、行高仍保留邮件原样式（用户只要求统一字体）。
/// </para>
/// </summary>
public static class MailBodyTypography
{
    /// <summary>CSS 字体栈（WebView2 路径使用）。</summary>
    public const string CssFontStack =
        "'Segoe UI','Microsoft YaHei UI','Microsoft YaHei',system-ui,-apple-system,'Noto Sans CJK SC',sans-serif";

    /// <summary>WPF 富文本/纯文本降级位面使用的字体族列表。</summary>
    public const string WpfFontFamily =
        "Segoe UI, Microsoft YaHei UI, Microsoft YaHei, Noto Sans CJK SC";

    /// <summary>等宽字体栈：只留给 &lt;pre&gt; / &lt;code&gt; 这类代码块，避免按比例字体排代码。</summary>
    public const string CssMonospaceStack = "Consolas,'Courier New',monospace";

    /// <summary>WPF 侧的等宽字体族。</summary>
    public const string WpfMonospaceFamily = "Consolas, Courier New";
}
