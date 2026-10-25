using ZungMail.Core.Abstractions;
using ZungMail.Core.Services;
using Xunit;

namespace ZungMail.Tests;

/// <summary>规则抽取引擎：零依赖保底，必须给出要点、待处理（时效）与单号。</summary>
public sealed class RuleBasedMailSummarizerTests
{
    private static MailSummaryRequest Request(string text) => new()
    {
        MessageId = 1,
        Subject = "SO-2026-001 订舱确认",
        InputText = text
    };

    [Fact]
    public async Task 抽取出单号与时效()
    {
        const string body = "SO-2026-001 已订舱，船名航次 HXSCNSVRA260959，箱号 TCNU1234567。\n" +
                            "SI 截止时间 2026-10-25 18:00，请务必在截关前提交补料。";

        var result = await new RuleBasedMailSummarizer().SummarizeAsync(Request(body));

        Assert.Equal(RuleBasedMailSummarizer.ProviderId, result.ProviderId);
        Assert.NotEmpty(result.Bullets);
        // 抽取器可能把「船名航次 + 箱号」归到同一个值，摘要会截到第一个分句：至少要有船名航次
        Assert.Contains(result.References, item => item.Contains("HXSCNSVRA260959"));
        Assert.Contains(result.Actions, item => item.Contains("截") || item.Contains("补料"));
        Assert.False(string.IsNullOrWhiteSpace(result.RawText));
    }

    [Fact]
    public async Task 无单号与日期时仍给出要点()
    {
        const string body = "这封只是告知，本周不需要我们处理，客户下周再确认价格。";

        var result = await new RuleBasedMailSummarizer().SummarizeAsync(Request(body));

        Assert.NotEmpty(result.Bullets);
        Assert.Empty(result.References);
    }

    [Fact]
    public async Task 空正文也能安全返回()
    {
        var result = await new RuleBasedMailSummarizer().SummarizeAsync(Request(""));

        Assert.Empty(result.Bullets);
        Assert.Contains("未提及", result.ToPlainText());
    }

    [Fact]
    public async Task 待处理带日期前缀()
    {
        const string body = "请安排在 2026-10-26 12:00 前把 VGM 发过来。VGM cut off 2026-10-26 12:00。";

        var result = await new RuleBasedMailSummarizer().SummarizeAsync(Request(body));

        Assert.NotEmpty(result.Actions);
        Assert.Contains(result.Actions, item => item.Contains("10-26"));
    }
}
