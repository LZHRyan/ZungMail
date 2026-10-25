using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 「仅清正文与附件」任务的展示与可执行动作：该模式不留隔离快照，
/// 所以清理记录里不能显示成可撤销 / 可彻底删除，否则按钮会点出空操作。
/// </summary>
public sealed class CleanupJobDisplayTests
{
    [Fact]
    public void 仅清正文任务_记录里标注模式且不可撤销()
    {
        var job = new CleanupJob
        {
            Id = 7,
            Mode = CleanupMode.Selected,
            Status = CleanupStatus.Completed,
            IsBodyOnly = true,
            TotalCount = 120,
            ProcessedCount = 120,
            TotalBytes = 3 * 1024 * 1024
        };

        Assert.False(job.CanUndo);
        Assert.False(job.CanPurge);
        Assert.Contains("仅清正文", job.ModeDisplay);
        Assert.Contains("不可撤销", job.StatusDisplay);
        Assert.Contains("已清空正文与附件", job.SummaryDisplay);
    }

    [Fact]
    public void 整封删除任务_仍可撤销与彻底删除()
    {
        var job = new CleanupJob
        {
            Id = 8,
            Mode = CleanupMode.Filter,
            Status = CleanupStatus.Completed,
            TotalCount = 10,
            ProcessedCount = 10,
            TotalBytes = 1024
        };

        Assert.True(job.CanUndo);
        Assert.True(job.CanPurge);
        Assert.DoesNotContain("仅清正文", job.ModeDisplay);
        Assert.Contains("待释放", job.SummaryDisplay);
    }

    [Fact]
    public void 仅清正文中断后仍可恢复()
    {
        var job = new CleanupJob
        {
            Id = 9,
            Mode = CleanupMode.Filter,
            Status = CleanupStatus.Cancelled,
            IsBodyOnly = true
        };

        Assert.True(job.CanResume);
        Assert.False(job.CanUndo);
        Assert.Contains("可恢复", job.StatusDisplay);
    }
}
