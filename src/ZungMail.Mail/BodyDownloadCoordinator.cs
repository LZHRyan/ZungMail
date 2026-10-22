using System.Collections.Concurrent;
using ZungMail.Core.Domain;

namespace ZungMail.Mail;

/// <summary>
/// 正文下载的协调器，解决「用户在等」与「后台预取」抢同一封邮件的问题：
/// <list type="number">
/// <item>单飞去重：同一封邮件并发请求只下一次，后来者 await 同一个 Task（不再开第二条连接、不再重复下载）。</item>
/// <item>交互计数：用户点开某封时计数 +1，后台预取据此让路（每封之间检查一次，不做线程抢占）。</item>
/// </list>
/// 只做「协调」，不碰协议与数据库，便于用单测锁住语义。
/// </summary>
public sealed class BodyDownloadCoordinator
{
    private readonly ConcurrentDictionary<long, Lazy<Task<EmailDetail>>> _inFlight = new();
    private int _interactive;

    /// <summary>当前是否有交互请求在等正文（后台预取看到就让路）。</summary>
    public bool HasInteractiveRequests => Volatile.Read(ref _interactive) > 0;

    /// <summary>正在进行中的下载数量（诊断与测试用）。</summary>
    public int InFlightCount => _inFlight.Count;

    /// <summary>
    /// 登记一次「用户在等」的下载；释放返回值时撤销登记。
    /// 用 <c>using</c> 包住整个下载过程，取消/异常路径也不会漏减计数。
    /// </summary>
    public IDisposable EnterInteractive()
    {
        Interlocked.Increment(ref _interactive);
        return new InteractiveScope(this);
    }

    /// <summary>取某封邮件正在进行的下载（没有则返回 false）。</summary>
    public bool TryGet(long emailId, out Task<EmailDetail> task)
    {
        if (_inFlight.TryGetValue(emailId, out var lazy))
        {
            task = lazy.Value;
            return true;
        }

        task = Task.FromResult<EmailDetail>(null!);
        return false;
    }

    /// <summary>
    /// 同一封邮件只启动一次下载：先到的执行 <paramref name="start"/>，后到的拿到同一个 Task。
    /// 任务结束（成功或失败）后自动从表里移除，下一次仍可重试。
    /// </summary>
    public Task<EmailDetail> GetOrStart(long emailId, Func<Task<EmailDetail>> start)
    {
        // 只把「赢家」的 Lazy 放进字典：并发竞争里输掉的那份工厂不会被执行
        var lazy = _inFlight.GetOrAdd(
            emailId,
            _ => new Lazy<Task<EmailDetail>>(start, LazyThreadSafetyMode.ExecutionAndPublication));

        var task = lazy.Value;
        if (task.IsCompleted)
        {
            Remove(emailId, lazy);
        }
        else
        {
            _ = task.ContinueWith(
                _ => Remove(emailId, lazy),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return task;
    }

    private void Remove(long emailId, Lazy<Task<EmailDetail>> lazy)
    {
        // 只在值仍是自己时移除，避免把后来重试登记的新任务删掉
        ((ICollection<KeyValuePair<long, Lazy<Task<EmailDetail>>>>)_inFlight)
            .Remove(new KeyValuePair<long, Lazy<Task<EmailDetail>>>(emailId, lazy));
    }

    private sealed class InteractiveScope : IDisposable
    {
        private BodyDownloadCoordinator? _owner;

        public InteractiveScope(BodyDownloadCoordinator owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null) Interlocked.Decrement(ref owner._interactive);
        }
    }
}
