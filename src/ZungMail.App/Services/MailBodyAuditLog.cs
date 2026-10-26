using System;
using System.Globalization;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using ZungMail.Infrastructure.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 正文组装入口：组装 + 「可见字数对账」。
///
/// 阅读区与独立读信窗口都从这里组装文档，避免两处各写一遍审计。
/// 对账只做日志：正文少一段时用户看到的是「对方好像没回复」，
/// 事后靠日志里的邮件 id 与丢失样本才能立刻定位到是哪一封、丢了哪段。
/// </summary>
public static class MailBodyAuditLog
{
    /// <summary>组装正文文档，并在可见文本明显变少时记一条 warning。</summary>
    public static MailBodyDocument Build(EmailDetail? detail, MailDocumentOptions options)
    {
        var document = MailBodyDocumentFactory.Build(detail, options);
        Report(detail, document);
        return document;
    }

    /// <summary>对账并在必要时写日志；明显丢失时返回审计结果，否则返回 null。</summary>
    public static MailBodyContentAudit? Report(EmailDetail? detail, MailBodyDocument document)
    {
        if (detail is null) return null;

        MailBodyContentAudit audit;
        try
        {
            audit = MailBodyContentAudit.Inspect(detail.BodyHtml, document.Html);
        }
        catch (Exception)
        {
            return null; // 审计只是辅助手段，绝不能因为它影响正文显示
        }

        if (!audit.Significant) return null;

        var sample = string.IsNullOrWhiteSpace(audit.LostSample) ? "（未取到样本）" : audit.LostSample;
        AppLog.Warn(string.Format(
            CultureInfo.InvariantCulture,
            "正文渲染疑似丢内容：邮件 {0}，源 {1} 字 → 渲染 {2} 字（丢 {3} 字 / {4:0}%），样本：{5}",
            detail.Id,
            audit.SourceChars,
            audit.RenderedChars,
            audit.LostChars,
            audit.LostRatio * 100,
            sample));

        return audit;
    }
}
