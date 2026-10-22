using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.Mail;

/// <summary>
/// 待发送队列处理器（FR-08）：到点发送、失败记录原因并保留队列最多重试 5 次，
/// 发送成功后沉淀联系人并尽力归档到服务器「已发送」。
/// </summary>
public sealed class OutboxProcessor : IOutboxProcessor
{
    private readonly IOutboxRepository _outbox;
    private readonly IAccountRepository _accounts;
    private readonly IContactRepository _contacts;
    private readonly IMailSendService _sender;
    private readonly ImapSyncService _imap;
    private readonly ILogger<OutboxProcessor> _logger;

    /// <summary>后台调度器与界面的「立即投递」可能同时触发，串行化以免同一封邮件被重复发送。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OutboxProcessor(
        IOutboxRepository outbox,
        IAccountRepository accounts,
        IContactRepository contacts,
        IMailSendService sender,
        ImapSyncService imap,
        ILogger<OutboxProcessor> logger)
    {
        _outbox = outbox;
        _accounts = accounts;
        _contacts = contacts;
        _sender = sender;
        _imap = imap;
        _logger = logger;
    }

    public event EventHandler<OutboxItem>? ItemSent;

    public event EventHandler<OutboxItem>? ItemFailed;

    public async Task<int> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = await _outbox.GetAllAsync(ct).ConfigureAwait(false);
            var recovered = 0;
            foreach (var item in items.Where(i => i.State == OutboxState.Sending).ToList())
            {
                const string reason = "发送结果未知：程序在投递过程中退出，服务器可能已接受该邮件。请先在「已发送」中确认，再决定是否重试。";
                await _outbox.UpdateStateAsync(item.Id, OutboxState.Uncertain, reason, item.Attempts, ct: ct)
                    .ConfigureAwait(false);

                item.State = OutboxState.Uncertain;
                item.LastError = reason;
                recovered++;
                ItemFailed?.Invoke(this, item);
            }

            if (recovered > 0)
            {
                _logger.LogWarning("已回收 {Count} 条中断的投递，状态标记为「发送结果未知」，不会自动重发", recovered);
            }
            return recovered;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ProcessDueCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ProcessDueCoreAsync(CancellationToken ct)
    {
        var due = await _outbox.GetDueAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        var sent = 0;

        foreach (var item in due)
        {
            ct.ThrowIfCancellationRequested();

            var account = await _accounts.GetAsync(item.AccountId, ct).ConfigureAwait(false);
            if (account is null || !account.HasCompleteConfig)
            {
                await FailAsync(item, account is null ? "账户不存在" : "账户配置不完整", ct).ConfigureAwait(false);
                continue;
            }

            // 停止使用的账户不发送定时邮件：保留在队列中，重新启用后继续投递。
            if (!account.IsEnabled) continue;

            try
            {
                await _outbox.UpdateStateAsync(item.Id, OutboxState.Sending, null, item.Attempts, ct: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await FailAsync(item, ex.Message, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await _sender.SendAsync(account, item, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await FailAsync(item, ex.Message, ct).ConfigureAwait(false);
                continue;
            }

            // 以下分支已进入「SMTP 已接受」语义：本地任何失败都不能按普通失败重试，否则会重复投递。
            try
            {
                await _outbox
                    .UpdateStateAsync(item.Id, OutboxState.Sent, null, item.Attempts + 1, ct: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                const string reason = "服务器已接受邮件，但本地状态写入失败。请到「已发送」确认后再决定是否重试。";
                _logger.LogError(ex, "邮件已被 SMTP 接受，但写入已发送状态失败：{Subject}", item.Subject);

                item.State = OutboxState.Uncertain;
                item.LastError = reason;
                ItemFailed?.Invoke(this, item);
                continue;
            }

            item.State = OutboxState.Sent;
            item.LastError = null;
            item.Attempts++;
            item.NextAttemptUtc = null;
            sent++;

            _logger.LogInformation("邮件已发送：{Subject} → {Count} 位收件人", item.Subject, item.To.Count);

            await CollectContactsAsync(item, ct).ConfigureAwait(false);
            await ArchiveAsync(account, item, ct).ConfigureAwait(false);

            ItemSent?.Invoke(this, item);
        }

        return sent;
    }

    private async Task FailAsync(OutboxItem item, string error, CancellationToken ct)
    {
        var attempts = item.Attempts + 1;

        // 失败退避：后台调度器每隔数十秒扫描一次，若立即重试会在一两分钟内耗尽全部尝试次数，
        // 因此按指数退避推迟下一次自动投递；次数用尽后不再自动重试，交由用户手动重试。
        var nextAttemptUtc = attempts < OutboxItem.MaxAttempts
            ? DateTimeOffset.UtcNow + OutboxItem.RetryDelay(attempts)
            : (DateTimeOffset?)null;

        await _outbox.UpdateStateAsync(item.Id, OutboxState.Failed, error, attempts, nextAttemptUtc, ct).ConfigureAwait(false);

        item.State = OutboxState.Failed;
        item.LastError = error;
        item.Attempts = attempts;
        item.NextAttemptUtc = nextAttemptUtc;

        if (nextAttemptUtc is { } next)
        {
            _logger.LogWarning("邮件发送失败（第 {Attempts}/{Max} 次）：{Error}；将于 {Next} 自动重试",
                attempts, OutboxItem.MaxAttempts, error, next.ToLocalTime());
        }
        else
        {
            _logger.LogWarning("邮件发送失败（第 {Attempts}/{Max} 次）：{Error}；自动重试次数已用尽，需手动重试",
                attempts, OutboxItem.MaxAttempts, error);
        }

        ItemFailed?.Invoke(this, item);
    }

    private async Task CollectContactsAsync(OutboxItem item, CancellationToken ct)
    {
        try
        {
            var recipients = item.To.Concat(item.Cc).Concat(item.Bcc)
                .Where(a => !string.IsNullOrWhiteSpace(a.Address))
                .ToList();

            if (recipients.Count > 0) await _contacts.CollectAsync(recipients, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "沉淀收件人失败");
        }
    }

    private async Task ArchiveAsync(Account account, OutboxItem item, CancellationToken ct)
    {
        try
        {
            using var message = SmtpSendService.BuildMessage(account, item);
            await _imap.AppendToSentAsync(account, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 归档失败不影响发送结果
            _logger.LogDebug(ex, "归档到已发送失败");
        }
    }
}
