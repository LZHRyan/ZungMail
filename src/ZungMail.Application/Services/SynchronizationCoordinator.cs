using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.Application.Services;

/// <summary>
/// 同步流程协调：串接同步引擎、转发进度事件，并把多账户结果汇总为可直接展示的文案。
/// 表示层只负责展示，不再自行处理同步异常与结果统计。
/// </summary>
public sealed class SynchronizationCoordinator
{
    private readonly IMailSyncService _sync;
    private readonly SyncExecutionGate _executionGate;
    private readonly ILogger<SynchronizationCoordinator> _logger;

    public SynchronizationCoordinator(
        IMailSyncService sync,
        SyncExecutionGate executionGate,
        ILogger<SynchronizationCoordinator> logger)
    {
        _sync = sync;
        _executionGate = executionGate;
        _logger = logger;
        _sync.ProgressChanged += (_, progress) => ProgressChanged?.Invoke(this, progress);
    }

    /// <summary>同步进度；可能由后台线程触发，订阅方需自行切回 UI 线程。</summary>
    public event EventHandler<SyncProgress>? ProgressChanged;

    /// <summary>执行一次全账户同步并返回明细；前后台入口由同一互斥门串行化。</summary>
    public Task<IReadOnlyList<SyncOutcome>> SyncWithOutcomesAsync(bool fullResync = false, CancellationToken ct = default)
        => _executionGate.RunAsync(token => _sync.SyncAllAsync(fullResync, token), ct);

    /// <summary>
    /// 后台轮询用：收件箱每轮必刷、其余文件夹滚动遍历，单轮有时间预算。
    /// 与前台同步共用同一道门，避免同一账户并发开两个 IMAP 会话。
    /// </summary>
    public Task<IReadOnlyList<SyncOutcome>> SyncIncrementAsync(CancellationToken ct = default)
        => _executionGate.RunAsync(token => _sync.SyncAllIncrementalAsync(token), ct);

    /// <summary>
    /// IMAP IDLE 探测到新邮件后的即时同步（只刷收件箱）。
    /// 门被前台/后台同步占着时直接放弃——那一轮同步本身就会把这封邮件收进来。
    /// </summary>
    public async Task<SyncOutcome?> SyncInboxWithOutcomeAsync(long accountId, CancellationToken ct = default)
    {
        var (ran, outcome) = await _executionGate
            .TryRunAsync(TimeSpan.FromSeconds(5), token => _sync.SyncInboxAsync(accountId, token), ct)
            .ConfigureAwait(false);

        return ran ? outcome : null;
    }

    /// <summary>
    /// 只同步某一个账户（侧栏账户行的「立即同步此账户」）：与全账户同步共用同一道门，
    /// 避免同一账户被并发开两条 IMAP 会话。
    /// </summary>
    public Task<SyncOutcome> SyncAccountAsync(long accountId, bool fullResync = false, CancellationToken ct = default)
        => _executionGate.RunAsync(token => _sync.SyncAccountAsync(accountId, fullResync, token), ct);

    /// <summary>执行一次全账户增量同步，返回状态栏文案。</summary>
    public async Task<string> SyncAsync(bool fullResync = false, CancellationToken ct = default)
    {
        try
        {
            var outcomes = await SyncWithOutcomesAsync(fullResync, ct).ConfigureAwait(false);
            var added = outcomes.Sum(o => o.NewMessages);
            var failures = outcomes.Where(o => !o.Success).ToList();

            return failures.Count == 0
                ? $"同步完成 · 新增 {added} 封"
                : $"部分账户同步失败：{failures[0].Error}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步失败");
            return "同步失败：" + ex.Message;
        }
    }
}
