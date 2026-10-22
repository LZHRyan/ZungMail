using System;
using System.Collections.Generic;
using System.Linq;

namespace ZungMail.App.Services;

/// <summary>搜索语法提示项：Token 为插入文本，NeedsValue 表示插入后还要继续输入值。</summary>
public sealed record SearchSyntaxItem(
    string Token,
    string Title,
    string Description,
    string Example,
    string Group,
    bool NeedsValue = true);

/// <summary>
/// 搜索框 <c>@</c> 语法选择器的纯逻辑：目录、过滤、触发检测与文本替换。
/// 与 WPF 控件解耦，便于单元测试。
/// </summary>
public static class SearchSyntaxCatalog
{
    public const char HalfWidthTrigger = '@';
    public const char FullWidthTrigger = '＠';

    public static IReadOnlyList<SearchSyntaxItem> All { get; } = new SearchSyntaxItem[]
    {
        new("from:", "发件人", "按发件人的邮箱地址或姓名筛选", "from:ops@example.com", "收件人"),
        new("subject:", "主题", "主题包含关键词；含空格时加英文引号", "subject:\"放舱通知\"", "内容"),
        new("filename:", "附件名", "按附件文件名筛选", "filename:SI", "内容"),
        new("has:attachment", "含附件", "只看带附件的邮件", "has:attachment", "内容", NeedsValue: false),
        new("is:unread", "未读邮件", "只看未读邮件", "is:unread", "状态", NeedsValue: false),
        new("is:read", "已读邮件", "只看已读邮件", "is:read", "状态", NeedsValue: false),
        new("is:starred", "星标邮件", "只看已加星标的邮件", "is:starred", "状态", NeedsValue: false),
        new("after:", "起始时间", "指定日期之后；也可用 7d / today", "after:2026-09-01", "时间"),
        new("after:7d", "最近 7 天", "最近 7 天内的邮件", "after:7d", "时间", NeedsValue: false),
        new("before:", "截止时间", "指定日期之前；也可用 today", "before:2026-10-01", "时间"),
        new("job:", "业务编号", "按 Job / 业务编号筛选", "job:JOB123", "货代单号"),
        new("booking:", "订舱号 / SO", "按订舱号、SO 或 BKG 筛选", "booking:SO123", "货代单号"),
        new("bl:", "提单号", "按 HBL / MBL / 提单号筛选", "bl:HBL123", "货代单号"),
        new("ctr:", "箱号", "按集装箱号筛选", "ctr:ABCD1234567", "货代单号"),
        new("vessel:", "船名航次", "按船名或航次筛选", "vessel:MAERSK", "货代单号"),
        new("port:", "港口", "按起运港 / 目的港筛选", "port:SHANGHAI", "货代单号"),
        new("ref:", "任意业务单号", "不区分单号类型，匹配任意业务标识", "ref:ABC123", "货代单号"),
        new("type:", "邮件类型", "按行业邮件类型筛选", "type:si", "邮件类型"),
        new("type:si", "SI / 补料", "只看 SI / 补料类邮件", "type:si", "邮件类型", NeedsValue: false),
        new("type:vgm", "VGM", "只看 VGM 类邮件", "type:vgm", "邮件类型", NeedsValue: false),
        new("type:bl", "提单草稿", "只看提单草稿类邮件", "type:bl", "邮件类型", NeedsValue: false),
        new("type:an", "到港通知", "只看 AN / 到港通知类邮件", "type:an", "邮件类型", NeedsValue: false)
    };

    /// <summary>按关键词过滤；空关键词返回全部。Token / 标题 / 说明 / 示例 / 分组都参与匹配。</summary>
    public static IReadOnlyList<SearchSyntaxItem> Filter(string? query)
    {
        var text = (query ?? string.Empty).Trim();
        if (text.Length == 0) return All;

        return All.Where(item =>
                item.Token.Contains(text, StringComparison.OrdinalIgnoreCase)
                || item.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                || item.Description.Contains(text, StringComparison.OrdinalIgnoreCase)
                || item.Example.Contains(text, StringComparison.OrdinalIgnoreCase)
                || item.Group.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// 从光标向前找最近的 <c>@</c> / <c>＠</c>。只有它位于开头或空白之后才算触发，
    /// 避免把邮箱地址里的 @ 当成菜单。
    /// </summary>
    public static bool TryGetTrigger(string? text, int caret, out int triggerStart, out string query)
    {
        triggerStart = 0;
        query = "";
        if (string.IsNullOrEmpty(text)) return false;

        caret = Math.Clamp(caret, 0, text.Length);
        var index = caret - 1;
        while (index >= 0 && !char.IsWhiteSpace(text[index])
               && text[index] != HalfWidthTrigger && text[index] != FullWidthTrigger)
        {
            index--;
        }

        if (index < 0) return false;
        if (text[index] != HalfWidthTrigger && text[index] != FullWidthTrigger) return false;
        if (index > 0 && !char.IsWhiteSpace(text[index - 1])) return false;

        triggerStart = index;
        query = text[(index + 1)..caret];
        return true;
    }

    /// <summary>用选中的语法替换触发片段，保留光标后的原有搜索词。</summary>
    public static (string Text, int Caret) Insert(
        string? text,
        int triggerStart,
        int caret,
        SearchSyntaxItem item)
    {
        text ??= string.Empty;
        triggerStart = Math.Clamp(triggerStart, 0, text.Length);
        caret = Math.Clamp(caret, triggerStart, text.Length);

        var prefix = text[..triggerStart];
        var suffix = text[caret..];
        var insertion = item.Token;

        // 完整语法（如 has:attachment）后面补一个空格方便继续输入；
        // 需要继续填值的语法（如 from:）把光标停在冒号后。
        if (!item.NeedsValue && (suffix.Length == 0 || !char.IsWhiteSpace(suffix[0])))
        {
            insertion += " ";
        }

        var newText = prefix + insertion + suffix;
        return (newText, prefix.Length + insertion.Length);
    }
}
