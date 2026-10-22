namespace ZungMail.Mail;

/// <summary>
/// IMAP 序位区间的换算工具。
///
/// 调用方使用「0 基偏移 + 数量」描述窗口（便于分页循环），而 MailKit 的
/// <c>FetchAsync(min, max)</c> 要求「1 基闭区间」。两者混用会直接抛
/// <see cref="ArgumentOutOfRangeException"/>，曾导致所有文件夹同步静默失败
/// （侧栏显示有信、列表却为空），因此把换算独立出来并覆盖边界测试。
/// </summary>
public static class UidWindow
{
    /// <summary>
    /// 把 0 基窗口换算为 1 基闭区间；窗口无效或完全越过末尾时返回 <c>null</c>。
    /// </summary>
    /// <param name="startOffset">0 基起始偏移（0 表示第一封）。</param>
    /// <param name="count">期望数量，必须为正。</param>
    /// <param name="availableCount">服务器已知总封数；小于等于 0 表示未知，不裁剪。</param>
    public static (int First, int Last)? ToFetchRange(int startOffset, int count, int availableCount)
    {
        if (startOffset < 0 || count < 1) return null;

        var first = startOffset + 1;
        var last = startOffset + count;

        if (availableCount > 0)
        {
            if (first > availableCount) return null;
            if (last > availableCount) last = availableCount;
        }

        return last < first ? null : (first, last);
    }
}
