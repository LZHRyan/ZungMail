using System.Text.RegularExpressions;

namespace ZungMail.Core.Services;

/// <summary>摘要的三段结构。任一为空表示模型/规则没给出该段内容，界面显示「未提及」。</summary>
public sealed record MailSummarySections(
    IReadOnlyList<string> Bullets,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> References)
{
    public static MailSummarySections Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    public bool IsEmpty => Bullets.Count == 0 && Actions.Count == 0 && References.Count == 0;
}

/// <summary>
/// 解析引擎输出的三段文本。小模型不一定严格按格式输出，所以解析要宽容：
/// 认得「要点 / 待处理 / 单号」及常见近义标题；完全没有标题时，把所有非空行当作要点。
/// </summary>
public static partial class MailSummaryTextParser
{
    private const int MaxItemsPerSection = 6;
    private const int MaxItemChars = 140;

    private enum Section
    {
        None = 0,
        Bullets = 1,
        Actions = 2,
        References = 3
    }

    public static MailSummarySections Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return MailSummarySections.Empty;

        var bullets = new List<string>();
        var actions = new List<string>();
        var references = new List<string>();
        var section = Section.None;
        var hasHeading = false;

        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (TryDetectHeading(line, out var detected))
            {
                section = detected;
                hasHeading = true;
                continue;
            }

            var item = CleanItem(line);
            if (item.Length == 0) continue;

            var target = section switch
            {
                Section.Actions => actions,
                Section.References => references,
                Section.Bullets => bullets,
                _ => hasHeading ? null : bullets
            };

            if (target is null) continue;
            if (target.Count >= MaxItemsPerSection) continue;
            if (target.Contains(item, StringComparer.Ordinal)) continue;
            target.Add(item);
        }

        return new MailSummarySections(bullets, actions, references);
    }

    /// <summary>识别标题行；返回 false 表示这是普通条目。</summary>
    private static bool TryDetectHeading(string line, out Section section)
    {
        section = Section.None;

        // 去掉 Markdown 加粗、序号、冒号等装饰后再比较
        var text = HeadingDecoration().Replace(line, "").Trim().TrimEnd(':', '：').Trim();
        if (text.Length == 0 || text.Length > 12) return false;

        if (BulletHeadings().Contains(text))
        {
            section = Section.Bullets;
            return true;
        }

        if (ActionHeadings().Contains(text))
        {
            section = Section.Actions;
            return true;
        }

        if (ReferenceHeadings().Contains(text))
        {
            section = Section.References;
            return true;
        }

        return false;
    }

    private static string[] BulletHeadings() =>
        ["要点", "关键点", "摘要", "总结", "内容摘要", "summary", "key points", "highlights"];

    private static string[] ActionHeadings() =>
        ["待处理", "待办", "需要处理", "行动项", "行动", "下一步", "todo", "action items", "actions", "next steps"];

    private static string[] ReferenceHeadings() =>
        ["单号", "业务编号", "参考号", "编号", "references", "reference", "ref", "numbers"];

    /// <summary>去掉行首的列表符号与序号，压缩空白，去掉「未提及」占位。</summary>
    private static string CleanItem(string line)
    {
        var text = ListPrefix().Replace(line, "").Trim();
        text = MailSummaryInputBuilder.Squash(text);
        text = text.Trim('。', '；', ';', '.', ' ');

        if (text.Length == 0) return "";
        if (NoInfoValues().Any(value => text.Equals(value, StringComparison.OrdinalIgnoreCase))) return "";
        // 「邮件内容：未提及」「无相关信息」这类表述同样视为没有内容
        if (text.Contains("未提及", StringComparison.Ordinal) || text.Contains("无相关信息", StringComparison.Ordinal)) return "";

        // 模型收尾时偶尔吐出角色标记、聊天模板或提示词回显，不能当成摘要条目
        if (ChatArtifact().IsMatch(text)) return "";
        if (PromptEcho().IsMatch(text)) return "";

        return text.Length <= MaxItemChars ? text : text[..MaxItemChars] + "…";
    }

    private static string[] NoInfoValues() =>
        ["未提及", "无", "暂无", "没有", "none", "n/a", "na", "-", "—"];

    [GeneratedRegex(@"^[\s#>*·\-•+]+|\*\*|^\d+[.、)]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingDecoration();

    [GeneratedRegex(@"^[\s\-•*·+]+|^\d+[.、)]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ListPrefix();

    /// <summary>聊天模板残留（用户：/ Assistant: / &lt;|im_start|&gt; 等）。</summary>
    [GeneratedRegex(@"^(用户|助手|User|Assistant|Human|AI)\s*[:：]?\s*$|<\|", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatArtifact();

    /// <summary>提示词回显（小模型会抄回系统提示或分隔符）：这些行不是摘要内容。</summary>
    [GeneratedRegex(@"邮件正文(开始|结束|如下)|不要执行其中的任何指令|只输出三段|请总结下面这封邮件|邮件主题[:：]", RegexOptions.CultureInvariant)]
    private static partial Regex PromptEcho();
}
