using System.Text.Json;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.Application.Services;

/// <summary>删除操作的执行结果：<paramref name="RemoteConfirmed"/> 为 false 时本地已删除但服务器尚未确认。</summary>
public readonly record struct MailActionResult(bool RemoteConfirmed, long OperationId);

/// <summary>批量已读结果。</summary>
public sealed record MailBulkActionResult(
    IReadOnlyList<(long Id, long FolderId, MessageFlags Flags)> Targets,
    bool RemoteConfirmed);

/// <summary>批量移动结果：<paramref name="Total"/> 为实际参与的封数，<paramref name="RemoteConfirmed"/> 为服务器已确认的封数。</summary>
public readonly record struct MailBatchMoveResult(int Total, int RemoteConfirmed);

/// <summary>
/// 批量移动的「本地已生效、待回写」句柄：界面据此立刻把行从列表移走，
/// 再把它交给后台推送，避免用户干等 IMAP 往返。
/// </summary>
public sealed record MailBatchMoveHandle(
    MailFolder Destination,
    IReadOnlyList<EmailSummary> Messages,
    IReadOnlyDictionary<long, long> Operations)
{
    public static MailBatchMoveHandle Empty { get; } =
        new(new MailFolder(), Array.Empty<EmailSummary>(), new Dictionary<long, long>());
}

/// <summary>待同步队列的一次重试汇总。</summary>
public readonly record struct PendingRetryResult(int Succeeded, int Failed);

/// <summary>
/// 待同步操作的一类聚合（状态栏「待同步 N 项」的明细用）：
/// 同一类操作有多少条、最早 / 最近一次的时间，以及最后一次失败原因。
/// </summary>
public sealed record PendingOperationSummary(
    string Label,
    int Count,
    DateTimeOffset Earliest,
    DateTimeOffset Latest,
    string? LastError);

/// <summary>
/// 邮件写操作（已读 / 星标 / 删除）的本地优先协调：
/// 先提交本地库并持久化「待同步操作」，再尽力回写服务器。
/// 回写失败时操作保持 Failed，界面可提示、可重试、可撤销，不再依赖下一次同步静默校准（NFR-R1）。
/// </summary>
public sealed class MailActionService
{
    /// <summary>已成功操作的保留时长，仅用于审计与排查，避免队列表无限增长。</summary>
    private static readonly TimeSpan SucceededRetention = TimeSpan.FromDays(7);

    private const string PushFailedMessage = "服务器未确认（可能离线或鉴权失败），将在下次同步重试";

    private readonly IMessageRepository _messages;
    private readonly IPendingOperationRepository _pending;
    private readonly IFolderRepository _folders;
    private readonly IMailSyncService _sync;
    private readonly ILogger<MailActionService> _logger;

    public MailActionService(
        IMessageRepository messages,
        IPendingOperationRepository pending,
        IFolderRepository folders,
        IMailSyncService sync,
        ILogger<MailActionService> logger)
    {
        _messages = messages;
        _pending = pending;
        _folders = folders;
        _sync = sync;
        _logger = logger;
    }

    /// <summary>启动时清理过期的成功记录。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            await _pending.PruneSucceededAsync(DateTimeOffset.UtcNow - SucceededRetention, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理待同步操作历史失败");
        }
    }

    public Task<int> GetPendingCountAsync(CancellationToken ct = default)
        => _pending.CountOutstandingAsync(ct);

    /// <summary>
    /// 待同步操作的明细（按「操作类型 + 目标文件夹」聚合成几行）。
    /// 状态栏只有一个数字时看不出卡在哪，这里给用户一个可读的分解。
    /// </summary>
    public async Task<IReadOnlyList<PendingOperationSummary>> GetPendingSummaryAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PendingOperation> operations;
        try
        {
            operations = await _pending.GetOutstandingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取待同步明细失败");
            return Array.Empty<PendingOperationSummary>();
        }

        if (operations.Count == 0) return Array.Empty<PendingOperationSummary>();

        // 目标文件夹名只查一次；文件夹已被删掉时用占位文案兜底
        var folderNames = new Dictionary<long, string>();

        var buckets = new Dictionary<string, (int Count, DateTimeOffset Earliest, DateTimeOffset Latest, string? Error)>(StringComparer.Ordinal);

        foreach (var operation in operations)
        {
            var label = await DescribeAsync(operation, folderNames, ct).ConfigureAwait(false);
            var created = operation.CreatedUtc;

            if (buckets.TryGetValue(label, out var bucket))
            {
                buckets[label] = (
                    bucket.Count + 1,
                    created < bucket.Earliest ? created : bucket.Earliest,
                    created > bucket.Latest ? created : bucket.Latest,
                    operation.LastError ?? bucket.Error);
            }
            else
            {
                buckets[label] = (1, created, created, operation.LastError);
            }
        }

        return buckets
            .Select(pair => new PendingOperationSummary(
                pair.Key, pair.Value.Count, pair.Value.Earliest, pair.Value.Latest, pair.Value.Error))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Label, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>把一条待同步操作翻译成「移动到「平安银行」」这类可读文案。</summary>
    private async Task<string> DescribeAsync(
        PendingOperation operation, Dictionary<long, string> folderNames, CancellationToken ct)
    {
        switch (operation.Kind)
        {
            case PendingOperationKind.MarkSeen:
                return "更新已读 / 未读";
            case PendingOperationKind.Flagged:
                return "更新星标";
            case PendingOperationKind.Deleted:
                return "删除";
            case PendingOperationKind.MarkSeenBatch:
                return "批量标记已读";
            case PendingOperationKind.Move:
                break;
            default:
                return "其它操作";
        }

        var destination = DeserializeMoveDestination(operation.Payload);
        if (destination is null) return "移动（目标已失效）";

        if (destination.Value.FolderId <= 0)
            return $"移动到「{PendingMoveGuard.KindLabel(destination.Value.Kind)}」";

        if (!folderNames.TryGetValue(destination.Value.FolderId, out var name))
        {
            var folder = await _folders.GetAsync(destination.Value.FolderId, ct).ConfigureAwait(false);
            name = folder?.DisplayName ?? "（文件夹已不存在）";
            folderNames[destination.Value.FolderId] = name;
        }

        return $"移动到「{name}」";
    }

    /// <summary>设置单封邮件的已读 / 未读状态，返回服务器是否已确认。</summary>
    public Task<bool> SetSeenAsync(EmailSummary message, bool seen, CancellationToken ct = default)
        => ApplyFlagAsync(message, MessageFlags.Seen, seen, PendingOperationKind.MarkSeen, ct);

    /// <summary>设置星标状态，返回服务器是否已确认。</summary>
    public Task<bool> SetStarredAsync(EmailSummary message, bool starred, CancellationToken ct = default)
        => ApplyFlagAsync(message, MessageFlags.Flagged, starred, PendingOperationKind.Flagged, ct);

    private async Task<bool> ApplyFlagAsync(
        EmailSummary message,
        MessageFlags flag,
        bool on,
        PendingOperationKind kind,
        CancellationToken ct)
    {
        var flags = on ? message.Flags | flag : message.Flags & ~flag;
        message.Flags = flags;

        await _messages.UpdateFlagsAsync(message.Id, flags, ct).ConfigureAwait(false);
        return await PushAsync(message.Id, flags, kind, payload: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除默认移动至垃圾箱而不是永久删除。源列表立即隐藏；服务器暂不可达时，
    /// 本地意图保留在待同步队列中，用户可以撤销。
    /// </summary>
    public Task<MailActionResult> DeleteAsync(EmailSummary message, CancellationToken ct = default)
        => MoveAsync(message, FolderKind.Trash, ct);

    /// <summary>移动、归档、标记垃圾邮件和恢复共用的本地优先入口。</summary>
    public async Task<MailActionResult> MoveAsync(
        EmailSummary message, FolderKind destination, CancellationToken ct = default)
    {
        var flags = message.Flags | MessageFlags.Deleted;
        message.Flags = flags;
        await _messages.UpdateFlagsAsync(message.Id, flags, ct).ConfigureAwait(false);

        var payload = JsonSerializer.Serialize(new MovePayload(destination));
        var operationId = await _pending.EnqueueAsync(new PendingOperation
        {
            EmailId = message.Id,
            Kind = PendingOperationKind.Move,
            Flags = flags,
            Payload = payload
        }, ct).ConfigureAwait(false);

        var result = await PushMoveAsync(operationId, message.Id, destination, ct).ConfigureAwait(false);
        if (result is MovePushResult.Moved or MovePushResult.MessageGone)
        {
            // 远端 MOVE 已成功（或服务器上已经没有这封）：本地旧 UID 已失效，
            // 下一次同步会以目标文件夹的新 UID 重新写入。
            await _messages.DeleteAsync(message.Id, ct).ConfigureAwait(false);
        }

        return new MailActionResult(result is MovePushResult.Moved, operationId);
    }

    public Task<MailActionResult> ArchiveAsync(EmailSummary message, CancellationToken ct = default)
        => MoveAsync(message, FolderKind.Archive, ct);

    public Task<MailActionResult> MarkAsJunkAsync(EmailSummary message, CancellationToken ct = default)
        => MoveAsync(message, FolderKind.Junk, ct);

    public Task<MailActionResult> RestoreToInboxAsync(EmailSummary message, CancellationToken ct = default)
        => MoveAsync(message, FolderKind.Inbox, ct);

    /// <summary>移动到用户指定的任意文件夹；目标文件夹必须存在且与邮件同属一个账户。</summary>
    public async Task<MailActionResult> MoveToFolderAsync(
        EmailSummary message, MailFolder destination, CancellationToken ct = default)
    {
        if (destination.Id == message.FolderId) return new MailActionResult(true, 0);

        var flags = message.Flags | MessageFlags.Deleted;
        message.Flags = flags;
        await _messages.UpdateFlagsAsync(message.Id, flags, ct).ConfigureAwait(false);

        var payload = JsonSerializer.Serialize(new MovePayload(destination.Kind, destination.Id));
        var operationId = await _pending.EnqueueAsync(new PendingOperation
        {
            EmailId = message.Id,
            Kind = PendingOperationKind.Move,
            Flags = flags,
            Payload = payload
        }, ct).ConfigureAwait(false);

        var result = await PushFolderMoveAsync(operationId, message.Id, destination, ct).ConfigureAwait(false);
        if (result is MovePushResult.Moved or MovePushResult.MessageGone)
        {
            await _messages.DeleteAsync(message.Id, ct).ConfigureAwait(false);
        }

        return new MailActionResult(result is MovePushResult.Moved, operationId);
    }

    /// <summary>
    /// 批量移动到同一目标文件夹：本地先全部生效并入队，随后一次批量回写把整批一起搬到服务器。
    /// 逐封移动要「建连→登录→SELECT→MOVE」各跑一遍，20 封就要十几秒；批量只跑一次连接。
    /// </summary>
    public async Task<MailBatchMoveResult> MoveToFolderBatchAsync(
        IReadOnlyList<EmailSummary> messages, MailFolder destination, CancellationToken ct = default)
    {
        var handle = await PrepareMoveToFolderBatchAsync(messages, destination, ct).ConfigureAwait(false);
        if (handle.Messages.Count == 0) return new MailBatchMoveResult(0, 0);
        return await PushMoveToFolderBatchAsync(handle, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量移动的「本地部分」：一次事务里把整批置 \Deleted 并写入待同步队列，然后立刻返回。
    /// 界面可以据此马上把行从列表移走（用户不必干等网络），再在后台调用
    /// <see cref="PushMoveToFolderBatchAsync"/> 回写服务器。
    /// </summary>
    public async Task<MailBatchMoveHandle> PrepareMoveToFolderBatchAsync(
        IReadOnlyList<EmailSummary> messages, MailFolder destination, CancellationToken ct = default)
    {
        var movable = messages.Where(message => message.FolderId != destination.Id).ToList();
        if (movable.Count == 0) return MailBatchMoveHandle.Empty;

        var flags = MessageFlags.Deleted;
        var payload = JsonSerializer.Serialize(new MovePayload(destination.Kind, destination.Id));

        // 一条 UPDATE 置 \Deleted（不再逐封写），再一次性入队（单事务），几十封也只有两次写
        await _messages.MarkDeletedAsync(movable.Select(message => message.Id).ToList(), ct).ConfigureAwait(false);
        foreach (var message in movable) message.Flags |= flags;

        var operations = movable
            .Select(message => new PendingOperation
            {
                EmailId = message.Id,
                Kind = PendingOperationKind.Move,
                Flags = message.Flags,
                Payload = payload
            })
            .ToList();

        var ids = await _pending.EnqueueRangeAsync(operations, ct).ConfigureAwait(false);

        return new MailBatchMoveHandle(
            destination,
            movable,
            operations.Zip(ids).ToDictionary(pair => pair.First.EmailId, pair => pair.Second));
    }

    /// <summary>
    /// 回写服务器：一次批量 MOVE，再按每封结果落队列终态、删本地旧行。
    /// 由界面层放到后台执行，耗时不影响其它操作。
    /// </summary>
    public async Task<MailBatchMoveResult> PushMoveToFolderBatchAsync(
        MailBatchMoveHandle handle, CancellationToken ct = default)
    {
        if (handle.Messages.Count == 0) return new MailBatchMoveResult(0, 0);

        IReadOnlyDictionary<long, MovePushResult> results;
        try
        {
            results = await _sync.MoveToFolderBatchAsync(
                handle.Messages.Select(message => message.Id).ToList(),
                handle.Destination.AccountId,
                handle.Destination.FullName,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 整批回写失败：全部留在待同步队列重试，行为与逐封路径一致
            _logger.LogWarning(ex, "批量移动到文件夹失败：{Folder}", handle.Destination.FullName);
            results = new Dictionary<long, MovePushResult>();
        }

        var confirmed = 0;
        foreach (var message in handle.Messages)
        {
            var operationId = handle.Operations.TryGetValue(message.Id, out var id) ? id : 0;
            var result = results.TryGetValue(message.Id, out var value) ? value : MovePushResult.Retryable;

            if (operationId > 0)
            {
                await _pending.UpdateStateAsync(operationId, PendingOperationState.Syncing, attempts: 1, ct: ct).ConfigureAwait(false);
                await SettleMoveAsync(operationId, result, attempts: 1, ct).ConfigureAwait(false);
            }

            if (result is MovePushResult.Moved or MovePushResult.MessageGone)
            {
                confirmed++;
                await _messages.DeleteAsync(message.Id, ct).ConfigureAwait(false);
            }
        }

        return new MailBatchMoveResult(handle.Messages.Count, confirmed);
    }

    /// <summary>
    /// 把查询条件命中的未读邮件全部标为已读（跨分页）；服务器回写在后台进行，不阻塞界面。
    /// </summary>
    public async Task<MailBulkActionResult> MarkAllSeenAsync(
        MessageQuery query, CancellationToken ct = default)
    {
        var targets = await _messages.MarkAllSeenAsync(query, ct).ConfigureAwait(false);
        if (targets.Count == 0) return new MailBulkActionResult(targets, true);

        var updates = targets.Select(t => (t.Id, t.Flags)).ToList();
        var payload = JsonSerializer.Serialize(updates.Select(u => new FlagUpdate(u.Id, (int)u.Flags)));
        var operationId = await _pending.EnqueueAsync(new PendingOperation
        {
            EmailId = 0,
            Kind = PendingOperationKind.MarkSeenBatch,
            Payload = payload
        }, ct).ConfigureAwait(false);

        await _pending.UpdateStateAsync(
            operationId, PendingOperationState.Syncing, attempts: 0, ct: ct).ConfigureAwait(false);

        var confirmed = true;
        try
        {
            await _sync.PushFlagsBatchAsync(updates, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            confirmed = false;
            _logger.LogWarning(ex, "批量回写已读标志失败");
        }

        await _pending.UpdateStateAsync(
            operationId,
            confirmed ? PendingOperationState.Succeeded : PendingOperationState.Failed,
            confirmed ? null : PushFailedMessage,
            attempts: 1,
            ct: ct).ConfigureAwait(false);

        return new MailBulkActionResult(targets, confirmed);
    }

    /// <summary>撤销一次尚未被服务器确认的删除：清除本地 \Deleted 标记并移除队列记录。</summary>
    public async Task<bool> UndoDeleteAsync(long operationId, CancellationToken ct = default)
    {
        var operation = await _pending.GetAsync(operationId, ct).ConfigureAwait(false);
        if (operation is null ||
            operation.Kind is not (PendingOperationKind.Deleted or PendingOperationKind.Move) ||
            operation.State == PendingOperationState.Succeeded)
        {
            return false;
        }

        var flags = operation.Flags & ~MessageFlags.Deleted;
        await _messages.UpdateFlagsAsync(operation.EmailId, flags, ct).ConfigureAwait(false);
        await _pending.RemoveAsync(operation.Id, ct).ConfigureAwait(false);

        // 服务器可能已收到 \Deleted（例如响应超时）；尽力清除，失败时由下次同步校准。
        try
        {
            await _sync.PushFlagsAsync(operation.EmailId, flags, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "撤销删除时回写服务器失败：email={Id}", operation.EmailId);
        }

        return true;
    }

    /// <summary>
    /// 重试全部未成功的操作（同步前调用）：成功即最终确认，失败保留在队列中。
    /// </summary>
    public async Task<PendingRetryResult> FlushPendingAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PendingOperation> operations;
        try
        {
            operations = await _pending.GetOutstandingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取待同步操作失败");
            return new PendingRetryResult(0, 0);
        }

        var succeeded = 0;
        var failed = 0;
        foreach (var operation in operations)
        {
            ct.ThrowIfCancellationRequested();
            if (await RetryAsync(operation, ct).ConfigureAwait(false)) succeeded++;
            else failed++;
        }

        return new PendingRetryResult(succeeded, failed);
    }

    private async Task<bool> RetryAsync(PendingOperation operation, CancellationToken ct)
    {
        if (operation.Kind == PendingOperationKind.MarkSeenBatch)
        {
            return await RetryBatchAsync(operation, ct).ConfigureAwait(false);
        }

        if (operation.Kind == PendingOperationKind.Move)
        {
            var destination = DeserializeMoveDestination(operation.Payload);
            if (destination is null)
            {
                // 载荷坏掉（历史版本写入格式变化）时没有可回写的内容，直接结案，别让它一直失败重试。
                await _pending.RemoveAsync(operation.Id, ct).ConfigureAwait(false);
                return true;
            }

            // 本地已经生效的移动（邮件已在目标文件夹 / 本地行已被清理）没有可回写的对象：
            // 再回写只会撞上「源 == 目标」永远失败，变成每次同步都重试的僵尸项。
            var detail = await _messages.GetDetailAsync(operation.EmailId, ct).ConfigureAwait(false);
            var localFolder = detail is null
                ? null
                : await _folders.GetAsync(detail.FolderId, ct).ConfigureAwait(false);

            var outcome = PendingMoveGuard.Evaluate(
                detail is not null,
                detail?.FolderId ?? 0,
                localFolder?.Kind,
                destination.Value.FolderId,
                destination.Value.Kind);

            if (outcome == PendingMoveOutcome.AlreadyApplied)
            {
                await _pending.RemoveAsync(operation.Id, ct).ConfigureAwait(false);
                return true;
            }

            if (destination.Value.FolderId > 0)
            {
                var folder = await _folders.GetAsync(destination.Value.FolderId, ct).ConfigureAwait(false);
                if (folder is null)
                {
                    // 目标文件夹已经被删掉：这条操作永远无法完成，结案。
                    await _pending.RemoveAsync(operation.Id, ct).ConfigureAwait(false);
                    return true;
                }

                var folderResult = await PushFolderMoveAsync(
                    operation.Id, operation.EmailId, folder, ct, operation.Attempts + 1).ConfigureAwait(false);
                return folderResult is not MovePushResult.Retryable;
            }

            var moveResult = await PushMoveAsync(
                operation.Id, operation.EmailId, destination.Value.Kind, ct, operation.Attempts + 1).ConfigureAwait(false);
            return moveResult is not MovePushResult.Retryable;
        }

        // 本地已不存在（被清理 / 账户删除）时没有可同步的对象，直接结案。
        if (await _messages.GetDetailAsync(operation.EmailId, ct).ConfigureAwait(false) is null)
        {
            await _pending.RemoveAsync(operation.Id, ct).ConfigureAwait(false);
            return true;
        }

        var confirmed = await PushFlagsAsync(
            operation.Id, operation.EmailId, operation.Flags, ct, operation.Attempts + 1).ConfigureAwait(false);

        if (confirmed && operation.Kind == PendingOperationKind.Deleted)
        {
            await _messages.DeleteAsync(operation.EmailId, ct).ConfigureAwait(false);
        }

        return confirmed;
    }

    private async Task<bool> RetryBatchAsync(PendingOperation operation, CancellationToken ct)
    {
        var updates = DeserializeBatch(operation.Payload);
        if (updates.Count == 0)
        {
            await _pending.RemoveAsync(operation.Id, ct).ConfigureAwait(false);
            return true;
        }

        await _pending.UpdateStateAsync(
            operation.Id, PendingOperationState.Syncing, attempts: operation.Attempts + 1, ct: ct).ConfigureAwait(false);

        var confirmed = true;
        try
        {
            await _sync.PushFlagsBatchAsync(updates, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            confirmed = false;
            _logger.LogWarning(ex, "重试批量已读回写失败");
        }

        await _pending.UpdateStateAsync(
            operation.Id,
            confirmed ? PendingOperationState.Succeeded : PendingOperationState.Failed,
            confirmed ? null : PushFailedMessage,
            operation.Attempts + 1,
            ct).ConfigureAwait(false);

        return confirmed;
    }

    /// <summary>回写移动操作并更新队列状态。</summary>
    private async Task<MovePushResult> PushMoveAsync(
        long operationId, long emailId, FolderKind destination, CancellationToken ct, int attempts = 1)
    {
        await _pending.UpdateStateAsync(
            operationId, PendingOperationState.Syncing, attempts: attempts, ct: ct).ConfigureAwait(false);

        var result = MovePushResult.Retryable;
        try
        {
            result = await _sync.MoveAsync(emailId, destination, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移动邮件失败：email={Id}, destination={Destination}", emailId, destination);
        }

        await SettleMoveAsync(operationId, result, attempts, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>按目标文件夹 id 回写移动（支持任意自定义文件夹）。</summary>
    private async Task<MovePushResult> PushFolderMoveAsync(
        long operationId, long emailId, MailFolder destination, CancellationToken ct, int attempts = 1)
    {
        await _pending.UpdateStateAsync(
            operationId, PendingOperationState.Syncing, attempts: attempts, ct: ct).ConfigureAwait(false);

        var result = MovePushResult.Retryable;
        try
        {
            result = await _sync.MoveToFolderAsync(emailId, destination.AccountId, destination.FullName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移动到文件夹失败：email={Id}, folder={Folder}", emailId, destination.FullName);
        }

        await SettleMoveAsync(operationId, result, attempts, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 落队列终态：只有「暂时无法确认」才留在待同步队列重试；
    /// 服务器已确认、或服务器上已经没有这封邮件（UID 失效 / 被别的客户端移走）都算结案。
    /// </summary>
    private async Task SettleMoveAsync(
        long operationId, MovePushResult result, int attempts, CancellationToken ct)
    {
        var settled = result is MovePushResult.Moved or MovePushResult.MessageGone;

        await _pending.UpdateStateAsync(
            operationId,
            settled ? PendingOperationState.Succeeded : PendingOperationState.Failed,
            settled ? null : PushFailedMessage,
            attempts,
            ct).ConfigureAwait(false);
    }

    /// <summary>入队 → 标为同步中 → 回写 → 落最终状态。</summary>
    private async Task<bool> PushAsync(
        long emailId,
        MessageFlags flags,
        PendingOperationKind kind,
        string? payload,
        CancellationToken ct)
    {
        var operationId = await _pending.EnqueueAsync(new PendingOperation
        {
            EmailId = emailId,
            Kind = kind,
            Flags = flags,
            Payload = payload
        }, ct).ConfigureAwait(false);

        return await PushFlagsAsync(operationId, emailId, flags, ct).ConfigureAwait(false);
    }

    /// <summary>回写已有队列项的标志位，并把结果写回状态机。</summary>
    private async Task<bool> PushFlagsAsync(
        long operationId, long emailId, MessageFlags flags, CancellationToken ct, int attempts = 1)
    {
        await _pending.UpdateStateAsync(
            operationId, PendingOperationState.Syncing, attempts: attempts, ct: ct).ConfigureAwait(false);

        var confirmed = false;
        try
        {
            confirmed = await _sync.PushFlagsAsync(emailId, flags, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "回写标志位失败：email={Id}", emailId);
        }

        await _pending.UpdateStateAsync(
            operationId,
            confirmed ? PendingOperationState.Succeeded : PendingOperationState.Failed,
            confirmed ? null : PushFailedMessage,
            attempts,
            ct).ConfigureAwait(false);

        return confirmed;
    }

    private static List<(long EmailId, MessageFlags Flags)> DeserializeBatch(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return new List<(long, MessageFlags)>();

        try
        {
            var items = JsonSerializer.Deserialize<List<FlagUpdate>>(payload);
            return items is null
                ? new List<(long, MessageFlags)>()
                : items.Select(i => (i.Id, (MessageFlags)i.Flags)).ToList();
        }
        catch (JsonException)
        {
            return new List<(long, MessageFlags)>();
        }
    }

    private static (FolderKind Kind, long FolderId)? DeserializeMoveDestination(string? payload)
    {
        try
        {
            var move = JsonSerializer.Deserialize<MovePayload>(payload ?? string.Empty);
            return move is null ? null : (move.Destination, move.DestinationFolderId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record FlagUpdate(long Id, int Flags);
    private sealed record MovePayload(FolderKind Destination, long DestinationFolderId = 0);
}
