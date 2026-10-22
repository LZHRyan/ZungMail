namespace ZungMail.Application.Services;

/// <summary>串行化前台手动同步与后台轮询，避免同一账户被并发 IMAP 会话重复同步。</summary>
public sealed class SyncExecutionGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await operation(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 尝试在 <paramref name="wait"/> 内拿到门，拿不到就放弃（不排队）。
    /// 给 IMAP IDLE 的即时同步用：正有一轮同步在跑时没必要再排队，那一轮本身就会把这封新邮件收进来。
    /// </summary>
    public async Task<(bool Ran, T? Value)> TryRunAsync<T>(
        TimeSpan wait, Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(wait, ct).ConfigureAwait(false)) return (false, default);
        try
        {
            return (true, await operation(ct).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }
}
