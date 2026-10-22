using System.Collections.Concurrent;

namespace ZungMail.UpdateServer.Security;

/// <summary>
/// 反馈提交的按来源限流：默认每小时每条来源 <see cref="UpdateServerOptions.FeedbackPerHourLimit"/> 条。
/// 只保留近一小时的命中记录，进程内存即可，重启后清零（反馈本身不敏感，不需要持久化）。
/// </summary>
public sealed class FeedbackRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly UpdateServerOptions _options;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();
    private readonly object _pruneLock = new();

    public FeedbackRateLimiter(UpdateServerOptions options) => _options = options;

    /// <summary>登记一次提交；超限时返回 false 并给出建议的重试间隔。</summary>
    public bool TryAcquire(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var limit = _options.FeedbackPerHourLimit;
        if (limit <= 0) return true;

        var now = DateTimeOffset.UtcNow;
        var queue = _hits.GetOrAdd(string.IsNullOrWhiteSpace(key) ? "unknown" : key, _ => new Queue<DateTimeOffset>());

        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= limit)
            {
                retryAfter = Window - (now - queue.Peek());
                if (retryAfter < TimeSpan.FromSeconds(1)) retryAfter = TimeSpan.FromSeconds(1);
                return false;
            }

            queue.Enqueue(now);
        }

        PruneIfNeeded(now);
        return true;
    }

    /// <summary>字典膨胀时清掉已经彻底空掉的来源，避免长期运行后一直堆 IP。</summary>
    private void PruneIfNeeded(DateTimeOffset now)
    {
        if (_hits.Count < 512) return;
        if (!Monitor.TryEnter(_pruneLock)) return;
        try
        {
            foreach (var pair in _hits)
            {
                lock (pair.Value)
                {
                    while (pair.Value.Count > 0 && now - pair.Value.Peek() > Window) pair.Value.Dequeue();
                    if (pair.Value.Count == 0) _hits.TryRemove(pair.Key, out _);
                }
            }
        }
        finally
        {
            Monitor.Exit(_pruneLock);
        }
    }
}
