using ZungMail.Mail;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 序位窗口换算的边界守护。
/// 这段逻辑曾把 0 基偏移直接传给 MailKit 的 1 基区间接口，导致空文件夹/整页同步
/// 抛 ArgumentOutOfRange 并被静默吞掉，表现为“侧栏有信、列表为空”。
/// </summary>
[Trait("Category", "Unit")]
public sealed class UidWindowTests
{
    [Theory]
    [InlineData(0, 50, 100, 1, 50)]     // 第一页：0 基 0 → 1 基 1
    [InlineData(50, 50, 100, 51, 100)]  // 第二页：紧接第一页，不重叠
    [InlineData(90, 50, 100, 91, 100)]  // 尾页：按总封数裁剪
    [InlineData(0, 50, 10, 1, 10)]      // 窗口大于总封数
    [InlineData(0, 25, 0, 1, 25)]       // 总数未知（0）时不裁剪，避免误判为空
    public void ToFetchRange_有效窗口_换算为1基闭区间(
        int start, int count, int available, int expectedFirst, int expectedLast)
    {
        var range = UidWindow.ToFetchRange(start, count, available);

        Assert.NotNull(range);
        Assert.Equal(expectedFirst, range!.Value.First);
        Assert.Equal(expectedLast, range.Value.Last);
    }

    [Theory]
    [InlineData(-1, 10, 100)]   // 负偏移
    [InlineData(0, 0, 100)]     // 数量为 0
    [InlineData(0, -5, 100)]    // 负数量
    [InlineData(100, 10, 100)]  // 起点正好越过末尾
    [InlineData(200, 10, 100)]  // 起点远越末尾
    public void ToFetchRange_无效或越界窗口_返回null(int start, int count, int available)
        => Assert.Null(UidWindow.ToFetchRange(start, count, available));
}
