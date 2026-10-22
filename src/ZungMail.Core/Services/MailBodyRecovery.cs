using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>
/// 正文补救入口（「重新获取正文」）的判定：什么情况下值得给用户这个按钮。
/// <para>
/// 只对「本该有正文、但现在没有」的邮件出现：
/// 已有正文 → 不出现；草稿 → 不出现（正文是本地内容，从服务器重拉没有意义）；
/// 正在下载 → 不出现（那时正文区显示的是进度条）。
/// </para>
/// 判定放在 Core 里做成纯函数，主窗口与独立阅读窗口共用一份规则，单测直接覆盖。
/// </summary>
public static class MailBodyRecovery
{
    /// <summary>正文为空、且不是草稿、也不在下载中时，才提供「重新获取正文」。</summary>
    public static bool ShouldOfferReload(EmailDetail? detail, bool isBodyLoading)
        => detail is not null
           && !isBodyLoading
           && !detail.IsDraftFlag
           && string.IsNullOrWhiteSpace(detail.BodyHtml)
           && string.IsNullOrWhiteSpace(detail.BodyText);
}
