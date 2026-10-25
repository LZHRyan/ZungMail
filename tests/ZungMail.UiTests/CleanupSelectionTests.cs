using System.Linq;
using ZungMail.App.ViewModels;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 清理列表的跨页勾选集合：单页全选 / 反选、整批全选 / 反选都得只动「当前筛选范围」，
/// 范围外的旧勾选（比如换了筛选条件）不能残留在待清理 id 里。
/// </summary>
[Trait("Category", "Ui")]
public sealed class CleanupSelectionTests
{
    private static readonly long[] Page1 = { 1, 2, 3 };
    private static readonly long[] Page2 = { 4, 5 };

    private static readonly long[] All = { 1, 2, 3, 4, 5 };

    [Fact]
    public void 单页勾选与取消_计数与导出同步()
    {
        var selection = new CleanupSelection();

        foreach (var id in Page1) selection.Set(id, true);
        Assert.Equal(3, selection.Count);
        Assert.True(selection.Contains(2));

        selection.Set(2, false);
        Assert.Equal(2, selection.Count);
        Assert.False(selection.Contains(2));
        Assert.Equal(new long[] { 1, 3 }, selection.Snapshot().OrderBy(id => id).ToArray());
    }

    [Fact]
    public void 单页全选后翻页再全选_两页都保留()
    {
        var selection = new CleanupSelection();

        foreach (var id in Page1) selection.Set(id, true);
        foreach (var id in Page2) selection.Set(id, true);

        Assert.Equal(5, selection.Count);
        Assert.Equal(All, selection.Snapshot().OrderBy(id => id).ToArray());
    }

    [Fact]
    public void 整批全选_以本批为准并清掉范围外残留()
    {
        var selection = new CleanupSelection();
        selection.Set(99, true);                        // 上一轮筛选残留

        selection.SelectAll(All);

        Assert.Equal(5, selection.Count);
        Assert.False(selection.Contains(99));
    }

    [Fact]
    public void 整批反选_原来勾的取消没勾的勾上且清掉范围外残留()
    {
        var selection = new CleanupSelection();
        selection.Set(2, true);
        selection.Set(99, true);                        // 范围外残留

        selection.InvertAll(All);

        Assert.Equal(4, selection.Count);
        Assert.False(selection.Contains(2));
        Assert.True(selection.Contains(1));
        Assert.True(selection.Contains(5));
        Assert.False(selection.Contains(99));
    }

    [Fact]
    public void 反选本页_只翻转本页不影响其它页()
    {
        var selection = new CleanupSelection();
        selection.Set(1, true);                         // 本页勾了 1
        foreach (var id in Page2) selection.Set(id, true);   // 第 2 页已勾

        // 本页反选：1 取消，2、3 勾上；第 2 页不受影响
        foreach (var id in Page1) selection.Set(id, !selection.Contains(id));

        Assert.Equal(4, selection.Count);
        Assert.False(selection.Contains(1));
        Assert.True(selection.Contains(2));
        Assert.True(selection.Contains(3));
        Assert.True(selection.Contains(4));
        Assert.True(selection.Contains(5));
    }

    [Fact]
    public void 清空后不再有待清理邮件()
    {
        var selection = new CleanupSelection();
        selection.SelectAll(All);

        selection.Clear();

        Assert.Equal(0, selection.Count);
        Assert.Empty(selection.Snapshot());
    }
}
