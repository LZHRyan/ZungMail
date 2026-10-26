using System;

namespace ZungMail.Core.Services;

/// <summary>
/// 正文「渲染前后可见字数」的对照检查。
///
/// 起因：一封「Re: Re:」邮件在别的客户端能看到对方新写的话，在阅读区却只剩下面的引用历史，
/// 看上去像对方没回复。这类「正文少一段」的毛病有两个特点：丢掉的部分往往是开头（脏 HTML 里
/// 内容写在 &lt;body&gt; 之前、body 标签重复或没闭合，或引用的历史邮件里嵌着一整份 &lt;html&gt;&lt;body&gt;），
/// 而且静态看代码很难判断是哪一封。所以这里做一次廉价对账：组装完成后比对源 HTML 与最终文档的
/// 可见文本，少得明显就记 warning，把邮件 id、丢了多少字、以及丢掉的那段开头原样写进日志。
///
/// 比对忽略空白差异（换行 / 缩进 / &amp;nbsp; 的取舍不影响结论），样本取自源文本原文，
/// 日志里读起来和邮件里看到的一模一样。
/// </summary>
public readonly record struct MailBodyContentAudit(int SourceChars, int RenderedChars, string LostSample)
{
    /// <summary>小于这么多字的正文不做判断：一句「收到」也能量出几十个百分点，全是噪声。</summary>
    public const int MinSourceChars = 200;

    /// <summary>丢失字数下限：低于这个量级的多半是空白与占位差异。</summary>
    public const int MinLostChars = 120;

    /// <summary>丢失比例下限：与 <see cref="MinLostChars"/> 同时满足才算「明显少了一段」。</summary>
    public const double MinLostRatio = 0.10;

    /// <summary>日志里附带的丢失样本长度。</summary>
    public const int SampleLength = 120;

    /// <summary>丢掉的字数。</summary>
    public int LostChars => Math.Max(0, SourceChars - RenderedChars);

    /// <summary>丢掉的比例（0–1）。</summary>
    public double LostRatio => SourceChars == 0 ? 0 : (double)LostChars / SourceChars;

    /// <summary>是否值得记一条 warning。</summary>
    public bool Significant => SourceChars >= MinSourceChars
        && LostChars >= MinLostChars
        && LostRatio >= MinLostRatio;

    /// <summary>比对源 HTML 与组装结果里的可见文本（去标签、去脚本样式、忽略空白差异）。</summary>
    public static MailBodyContentAudit Inspect(string? sourceHtml, string? renderedDocument)
    {
        var source = HtmlTextExtractor.ToPlainText(sourceHtml);
        var rendered = HtmlTextExtractor.ToPlainText(renderedDocument);

        return new MailBodyContentAudit(
            VisibleChars(source),
            VisibleChars(rendered),
            Sample(source, rendered));
    }

    private static int VisibleChars(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c)) count++;
        }

        return count;
    }

    /// <summary>
    /// 从第一处不一致往后取一段原文：开头被吞时样本就是被吞掉的那句，
    /// 尾部被吞时样本是被吞掉的尾部 —— 两种情况在日志里一眼能区分。
    /// </summary>
    private static string Sample(string source, string rendered)
    {
        int i = 0, j = 0;
        while (i < source.Length && j < rendered.Length)
        {
            if (char.IsWhiteSpace(source[i])) { i++; continue; }
            if (char.IsWhiteSpace(rendered[j])) { j++; continue; }
            if (source[i] != rendered[j]) break;
            i++;
            j++;
        }

        while (i < source.Length && char.IsWhiteSpace(source[i])) i++;
        if (i >= source.Length) return string.Empty;

        return source.Substring(i, Math.Min(SampleLength, source.Length - i));
    }
}
