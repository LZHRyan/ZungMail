namespace ZungMail.App.Views;

/// <summary>列表拖拽载荷：只传递稳定的邮件主键，避免跨进程或跨视图持有可变模型。</summary>
public sealed record MailDragPayload(IReadOnlyList<long> MessageIds);
