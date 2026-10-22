using ZungMail.App.Services;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>多选后直接拖拽：按下已选中的行时不能让 ListBox 把选区收成单选。</summary>
[Trait("Category", "Ui")]
public sealed class MailDragSelectionTests
{
    [Theory]
    [InlineData(true, 3, false, true)]   // 已选中行 + 多选 + 无修饰键 → 保住选区，整批拖
    [InlineData(true, 2, false, true)]   // 两封也一样
    [InlineData(true, 3, true, false)]   // 按着 Ctrl / Shift：保留原生选择语义
    [InlineData(true, 1, false, false)]  // 只有一行：不需要压住
    [InlineData(false, 3, false, false)] // 按在未选中的行上：正常单选
    public void 是否保住多选选区(bool itemIsSelected, int selectedCount, bool hasModifier, bool expected)
        => Assert.Equal(expected, MailDragSelection.ShouldPreserveSelection(itemIsSelected, selectedCount, hasModifier));
}
