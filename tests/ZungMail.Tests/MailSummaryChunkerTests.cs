using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>长邮件分段：切点落在段落/句子边界、单段不超限、超出段数上限时标记未覆盖。</summary>
public sealed class MailSummaryChunkerTests
{
    [Fact]
    public void 短正文只有一段()
    {
        var plan = MailSummaryChunker.Split("请安排订舱，10-25 前提交 SI。", chunkChars: 4000, maxChunks: 6);

        Assert.Single(plan.Chunks);
        Assert.False(plan.IsMultiChunk);
        Assert.True(plan.Covered);
    }

    [Fact]
    public void 超长正文按段切分且每段不超限()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"第 {i} 段：{new string('字', 480)}"));

        var plan = MailSummaryChunker.Split(text, chunkChars: 2000, maxChunks: 10);

        Assert.True(plan.Count > 1);
        Assert.All(plan.Chunks, chunk => Assert.True(chunk.Text.Length <= 2000, $"第 {chunk.Index} 段 {chunk.Text.Length} 字"));
        Assert.True(plan.Covered);
        Assert.Equal(text.Length, plan.CoveredChars);
    }

    [Fact]
    public void 优先在段落边界切分()
    {
        // 每段 700 字：硬切在 800 会把第二段切出 100 字，按段落切则第一块只有第一段
        var first = new string('甲', 700);
        var second = new string('乙', 700);
        var text = first + "\n\n" + second;

        var plan = MailSummaryChunker.Split(text, chunkChars: 800, maxChunks: 6);

        Assert.True(plan.Count > 1);
        Assert.Equal(first, plan.Chunks[0].Text);
        Assert.DoesNotContain('乙', plan.Chunks[0].Text);
        Assert.Contains('乙', plan.Chunks[1].Text);
    }

    [Fact]
    public void 超过段数上限时标记未覆盖并只保留前几段()
    {
        var text = new string('乙', 12000);

        var plan = MailSummaryChunker.Split(text, chunkChars: 4000, maxChunks: 2);

        Assert.Equal(2, plan.Count);
        Assert.False(plan.Covered);
        Assert.Equal(8000, plan.CoveredChars);
        Assert.Equal(12000, plan.TotalChars);
    }

    [Fact]
    public void 空正文返回空计划()
    {
        var plan = MailSummaryChunker.Split("   ", chunkChars: 4000, maxChunks: 6);

        Assert.Empty(plan.Chunks);
        Assert.True(plan.Covered);
    }

    [Fact]
    public void 单段编号从1开始且总数正确()
    {
        var text = new string('丙', 9000);

        var plan = MailSummaryChunker.Split(text, chunkChars: 3000, maxChunks: 6);

        Assert.Equal(3, plan.Count);
        Assert.Equal([1, 2, 3], plan.Chunks.Select(chunk => chunk.Index).ToArray());
        Assert.All(plan.Chunks, chunk => Assert.Equal(3, chunk.Total));
    }
}
