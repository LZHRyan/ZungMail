using System.Text.RegularExpressions;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>
/// 规则抽取引擎：零依赖、毫秒级，所以永远是「保底引擎」。
/// 不生成新句子，只做三件事：挑关键句当要点、把带祈使词的句子当待处理、复用现有抽取器列出单号与时效。
/// </summary>
public sealed partial class RuleBasedMailSummarizer : IMailSummaryProvider
{
    public const string ProviderId = "rule-based";

    public string Id => ProviderId;

    public string DisplayName => "规则抽取";

    public Task<MailSummaryProviderStatus> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new MailSummaryProviderStatus { Available = true });

    public Task<MailSummaryResult> SummarizeAsync(
        MailSummaryRequest request,
        IProgress<MailSummaryProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new MailSummaryProgress("正在提取要点", 0.3));
        var started = Environment.TickCount64;

        var text = request.InputText ?? "";
        var now = DateTimeOffset.Now;

        var sentences = SplitSentences(text);
        var deadlines = FreightDeadlineExtractor.Extract(request.Subject, text, now);
        var references = FreightReferenceExtractor.Extract(request.Subject, text);

        var bullets = PickBullets(sentences, request.Length);
        var actions = PickActions(sentences, deadlines, now);

        var referenceLines = references
            .Select(reference => FormatReference(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var sections = new MailSummarySections(bullets, actions, referenceLines);
        var elapsed = Environment.TickCount64 - started;

        progress?.Report(new MailSummaryProgress("完成", 1, $"规则抽取用时 {elapsed} ms"));

        return Task.FromResult(new MailSummaryResult
        {
            MessageId = request.MessageId,
            ProviderId = Id,
            ProviderName = DisplayName,
            Bullets = sections.Bullets,
            Actions = sections.Actions,
            References = sections.References,
            RawText = new MailSummaryResult
            {
                MessageId = request.MessageId,
                ProviderId = Id,
                ProviderName = DisplayName,
                Bullets = sections.Bullets,
                Actions = sections.Actions,
                References = sections.References
            }.ToPlainText(),
            Truncated = request.Truncated,
            InputChars = request.InputChars,
            ElapsedMs = elapsed
        });
    }

    /// <summary>
    /// 单号展示：抽取器偶尔把「船名航次 + 开船时间 + 箱号」整段当成一个值，
    /// 摘要里截到第一个分句，避免一行塞半封邮件。
    /// </summary>
    private static string FormatReference(FreightReference reference)
    {
        var value = reference.Value.Trim();

        // 摘要里的「单号」必须是原子的：抽取器的值偶尔带整句描述，一律截到第一个分句
        var cut = value.IndexOfAny(['，', ',', '；', ';']);
        if (cut > 0) value = value[..cut].Trim();

        return $"{reference.KindLabel} {value}";
    }

    /// <summary>挑关键句：含业务词/祈使词加权，靠前的句子略微加权，保持原文顺序输出。</summary>
    private static List<string> PickBullets(IReadOnlyList<string> sentences, MailSummaryLength length)
    {
        var take = length switch
        {
            MailSummaryLength.Short => 3,
            MailSummaryLength.Detailed => 5,
            _ => 4
        };

        var scored = new List<(int Index, double Score)>();
        for (var i = 0; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            if (sentence.Length < 6) continue;

            double score = 1.0;
            foreach (var keyword in ClueKeywords())
            {
                if (sentence.Contains(keyword, StringComparison.OrdinalIgnoreCase)) score += 1.4;
            }

            if (sentence.Length > 40) score += 0.6;
            score -= i * 0.05;

            scored.Add((i, score));
        }

        return scored
            .OrderByDescending(entry => entry.Score)
            .Take(take)
            .OrderBy(entry => entry.Index)
            .Select(entry => Clip(sentences[entry.Index]))
            .ToList();
    }

    /// <summary>待处理：抽取到的时效节点（带日期，最能指导行动）+ 带祈使动词的句子。</summary>
    private static List<string> PickActions(
        IReadOnlyList<string> sentences,
        IReadOnlyList<FreightDeadline> deadlines,
        DateTimeOffset now)
    {
        var actions = new List<string>();

        foreach (var deadline in deadlines.Take(4))
        {
            actions.Add($"{deadline.KindLabel} {deadline.DueDayDisplay}（{deadline.Countdown(now)}）");
        }

        // 长邮件里同一句话会反复出现（只差箱号/金额）：按「去掉数字与标点」的骨架去重，
        // 并限制自然语言待办最多 3 条，避免把 6 条位置全占满、盖掉时效节点前面的信息价值。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keywordActions = 0;

        foreach (var sentence in sentences)
        {
            if (keywordActions >= 3) break;
            if (!ActionKeywords().Any(keyword => sentence.Contains(keyword, StringComparison.OrdinalIgnoreCase))) continue;

            var skeleton = Skeleton(sentence);
            if (!seen.Add(skeleton)) continue;

            var item = Clip(sentence);
            if (actions.Contains(item, StringComparer.Ordinal)) continue;

            actions.Add(item);
            keywordActions++;
        }

        return actions.Take(6).ToList();
    }

    /// <summary>去数字与标点后的句子骨架，用于判断「同一句话的不同变体」。</summary>
    private static string Skeleton(string text)
        => new string(text.Where(ch => !char.IsDigit(ch) && !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch)).ToArray());

    private static string[] ClueKeywords() =>
    [
        "船", "航线", "开船", "到港", "截关", "截补料", "截vgm", "cut off", "cut-off",
        "箱", "柜", "提单", "hbl", "mbl", "booking", "舱位",
        "费用", "价格", "报价", "付款", "美金", "usd", "rmb", "cny", "发票",
        "请", "需", "务必", "尽快", "确认", "安排", "提交", "回复", "注意", "提醒"
    ];

    private static string[] ActionKeywords() =>
        ["请", "需", "务必", "尽快", "安排", "提交", "确认", "回复", "提供", "补充", "注意", "截止"];

    /// <summary>拆分句子：中文句末标点、换行、分号都算边界。</summary>
    private static List<string> SplitSentences(string text)
    {
        var parts = SentenceSplitter().Split(text);
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var sentence = MailSummaryInputBuilder.Squash(part);
            if (sentence.Length > 0) result.Add(sentence);
        }

        return result;
    }

    private static string Clip(string text, int max = 120)
        => text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex(@"[。！？；;!?\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceSplitter();
}
