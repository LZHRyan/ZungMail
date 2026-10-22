using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>
/// 邮件动作的可用性判定（界面按钮与命令共用，避免两处规则漂移）。
/// </summary>
public static class MailActionGuards
{
    /// <summary>
    /// 能否回复 / 回复全部 / 转发：草稿还没发出去，这些动作没有意义，
    /// 只能双击草稿继续编辑或直接删除。
    /// </summary>
    public static bool CanReplyOrForward(EmailSummary? message)
        => message is { IsDraftFlag: false };

    /// <summary>按「详情优先、列表行兜底」的取值判断，详情还没加载出来时也能立即禁用。</summary>
    public static bool CanReplyOrForward(EmailDetail? detail, EmailSummary? fallback)
        => CanReplyOrForward((EmailSummary?)detail ?? fallback);
}
