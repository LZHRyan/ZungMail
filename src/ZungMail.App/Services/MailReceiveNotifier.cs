using ZungMail.Core.Domain;

namespace ZungMail.App.Services;

/// <summary>后台收信与托盘 UI 之间的轻量通知总线，不依赖具体窗口。</summary>
public sealed class MailReceiveNotifier
{
    public event EventHandler<NewMailNotification>? NewMailReceived;

    /// <summary>
    /// 一次后台同步（整轮）结束：不论有没有新邮件都会触发，主窗口据此刷新列表与侧栏未读数。
    /// 与 <see cref="NewMailReceived"/>（只在新邮件时给托盘弹气泡）区分开。
    /// </summary>
    public event EventHandler<IReadOnlyList<SyncOutcome>>? SyncCompleted;

    public void Publish(SyncOutcome outcome)
    {
        if (!outcome.Success || outcome.NewMessages <= 0) return;
        NewMailReceived?.Invoke(this, new NewMailNotification(
            outcome.AccountId,
            outcome.AccountName,
            outcome.NewMessages));
    }

    public void PublishCompleted(IReadOnlyList<SyncOutcome> outcomes)
    {
        if (outcomes.Count == 0) return;
        SyncCompleted?.Invoke(this, outcomes);
    }
}

/// <summary>一次后台收信的可展示摘要。</summary>
public sealed record NewMailNotification(long AccountId, string AccountName, int Count)
{
    public string Text => Count == 1
        ? $"{AccountName} 收到 1 封新邮件"
        : $"{AccountName} 收到 {Count} 封新邮件";
}
