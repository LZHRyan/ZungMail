namespace ZungMail.App.Services;

/// <summary>邮件列表拖拽的选区规则（与界面无关，便于单测）。</summary>
public static class MailDragSelection
{
    /// <summary>
    /// 按下鼠标时要「保住」现有选区、不让 ListBox 把多选收成单选的条件：
    /// 该行本来就在选区里、当前是多选、且没有按 Ctrl / Shift（带修饰键时保持原生选择语义）。
    /// 满足时整批拖拽才成立，用户不必在拖动过程中一直按着修饰键。
    /// </summary>
    public static bool ShouldPreserveSelection(bool itemIsSelected, int selectedCount, bool hasSelectionModifier)
        => itemIsSelected && selectedCount > 1 && !hasSelectionModifier;
}
