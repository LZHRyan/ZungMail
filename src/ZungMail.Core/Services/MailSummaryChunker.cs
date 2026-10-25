namespace ZungMail.Core.Services;

/// <summary>一段待总结的正文，以及整封邮件的覆盖情况。</summary>
public sealed record MailSummaryChunk(int Index, int Total, string Text);

/// <summary>分段结果：<see cref="Chunks"/> 至少一段；<see cref="Covered"/> 为 false 表示尾部没覆盖到。</summary>
public sealed record MailSummaryChunkPlan(IReadOnlyList<MailSummaryChunk> Chunks, bool Covered, int CoveredChars, int TotalChars)
{
    public int Count => Chunks.Count;

    public bool IsMultiChunk => Chunks.Count > 1;
}

/// <summary>
/// 长邮件分段：优先在段落边界切，其次句号，最后才硬切。
/// <para>
/// 0.5B 小模型的上下文只有几千 token，直接截断会丢掉邮件后半段的船期、费用与截止时间；
/// 分段后逐段总结再合并，既保住覆盖面，也把单次推理控制在 2–4 秒。
/// </para>
/// </summary>
public static class MailSummaryChunker
{
    /// <summary>
    /// 按 <paramref name="chunkChars"/> 切分；超过 <paramref name="maxChunks"/> 的部分丢弃并把
    /// <see cref="MailSummaryChunkPlan.Covered"/> 标成 false（界面据此提示「只覆盖前 N 字」）。
    /// </summary>
    public static MailSummaryChunkPlan Split(string? text, int chunkChars, int maxChunks)
    {
        var source = text ?? "";
        var limit = Math.Clamp(chunkChars, 500, 8000);
        var max = Math.Clamp(maxChunks, 1, 12);

        if (source.Length == 0)
        {
            return new MailSummaryChunkPlan(Array.Empty<MailSummaryChunk>(), Covered: true, 0, 0);
        }

        var pieces = Cut(source, limit);
        var taken = pieces.Count > max ? pieces.Take(max).ToList() : pieces;
        // 用「源文本消耗长度」而不是 trim 后的长度：段落之间的换行被裁掉，不能算作漏覆盖
        var coveredChars = taken.Sum(piece => piece.Consumed);

        var chunks = taken
            .Select((piece, index) => new MailSummaryChunk(index + 1, taken.Count, piece.Text))
            .ToList();

        return new MailSummaryChunkPlan(chunks, Covered: taken.Count == pieces.Count, coveredChars, source.Length);
    }

    /// <summary>把整段文本切成不超过 limit 的小块，尽量落在段落/句子边界上。</summary>
    private static List<(string Text, int Consumed)> Cut(string text, int limit)
    {
        var result = new List<(string Text, int Consumed)>();
        var start = 0;

        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= limit)
            {
                var tail = text[start..].Trim();
                if (tail.Length > 0) result.Add((tail, remaining));
                break;
            }

            var window = text.AsSpan(start, limit);
            var cut = FindBreak(window, out var breakLength);
            var take = cut > 0 ? cut + breakLength : limit;

            var piece = text.Substring(start, take).Trim();
            if (piece.Length > 0) result.Add((piece, take));
            start += take;
        }

        return result;
    }

    /// <summary>在窗口内找最靠后的自然断点（段落 &gt; 换行 &gt; 句末标点 &gt; 分号/逗号）。</summary>
    private static int FindBreak(ReadOnlySpan<char> window, out int breakLength)
    {
        breakLength = 1;

        // 段落
        var index = window.LastIndexOf("\n\n".AsSpan());
        if (index > window.Length / 3)
        {
            breakLength = 2;
            return index;
        }

        // 换行
        index = window.LastIndexOf('\n');
        if (index > window.Length / 3) return index;

        // 句末标点
        foreach (var mark in new[] { '。', '！', '？', '!', '?', '.' })
        {
            index = window.LastIndexOf(mark);
            if (index > window.Length / 3) return index + 1;
        }

        // 分句
        foreach (var mark in new[] { '；', ';', '，', ',' })
        {
            index = window.LastIndexOf(mark);
            if (index > window.Length / 2) return index + 1;
        }

        return -1;
    }
}
