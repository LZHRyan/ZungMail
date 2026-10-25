using MimeKit;

namespace ZungMail.Mail;

/// <summary>退信细节：失败收件人 / SMTP 状态码 / 诊断原因（都可能为空串）。</summary>
public sealed record BounceDetails(string Recipient, string Status, string Detail);

/// <summary>
/// 从退信邮件（RFC 3464 DSN）的 <c>message/delivery-status</c> 段里取失败细节。
/// <para>
/// 只有正文下载后才有完整 MIME，因此解析放在下载链路里顺带完成，判定本身仍靠
/// <see cref="ZungMail.Core.Services.BounceMailPolicy"/>（入库时已完成）。
/// 解析不出来一律返回 null，由调用方保留「退信」标识并留空细节。
/// </para>
/// </summary>
public static class BounceDsnParser
{
    /// <summary>解析失败细节；不是 DSN 或没有失败收件人时返回 null。</summary>
    public static BounceDetails? Parse(MimeMessage message)
    {
        var statusParts = Descendants(message.Body).OfType<MessageDeliveryStatus>().ToList();
        if (statusParts.Count == 0) return null;

        BounceDetails? fallback = null;
        foreach (var part in statusParts)
        {
            foreach (var group in part.StatusGroups)
            {
                var action = Value(group, "Action");
                var status = Value(group, "Status");
                var recipient = StripTypePrefix(Value(group, "Final-Recipient"));
                if (recipient.Length == 0) recipient = StripTypePrefix(Value(group, "Original-Recipient"));
                var detail = StripTypePrefix(Value(group, "Diagnostic-Code"));

                var isFailed = action.Equals("failed", StringComparison.OrdinalIgnoreCase);
                var isPermanent = status.StartsWith('5');

                if (!isFailed && !isPermanent) continue;

                var details = new BounceDetails(recipient, status, detail);
                if (isFailed) return details;

                // 没有 Action 头时，5.x.x 永久失败也算结论；先记下来继续找更明确的
                fallback ??= details;
            }
        }

        return fallback;
    }

    private static IEnumerable<MimeEntity> Descendants(MimeEntity? entity)
    {
        if (entity is null) yield break;

        yield return entity;

        switch (entity)
        {
            case Multipart multipart:
                foreach (var child in multipart)
                {
                    foreach (var descendant in Descendants(child)) yield return descendant;
                }

                break;

            case MessagePart nested:
                foreach (var descendant in Descendants(nested.Message?.Body)) yield return descendant;
                break;
        }
    }

    private static string Value(HeaderList group, string name) =>
        Collapse(group[name] ?? string.Empty);

    /// <summary>去掉 <c>rfc822; </c> / <c>smtp; </c> 这类类型前缀（RFC 3464 的字段格式）。</summary>
    private static string StripTypePrefix(string value)
    {
        var semicolon = value.IndexOf(';');
        return semicolon >= 0 ? Collapse(value[(semicolon + 1)..]) : value;
    }

    /// <summary>多行抬头（诊断码常折行）压成一行，便于直接上界面。</summary>
    private static string Collapse(string value)
    {
        var parts = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(part => part.Trim())).Trim();
    }
}
