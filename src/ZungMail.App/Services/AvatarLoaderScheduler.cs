using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Avatars;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 联系人头像的一次性懒加载：启动后等界面彻底闲下来，再按邮箱地址查 Gravatar，
/// 把拿到的头像顶到首字母色块前面显示。
/// <para>
/// <b>一个运行周期只尝试一次</b>（<see cref="BackgroundService.ExecuteAsync"/> 进程内只跑一遍），
/// 单轮上限 300 个地址、并发 3、单次 5 秒超时；查不到就写 7 天有效的 miss 标记，下次启动不再问。
/// 开关在设置 → 通用（默认开）。
/// </para>
/// </summary>
public sealed class AvatarLoaderScheduler : BackgroundService
{
    /// <summary>启动后等待时间：先让首屏、增量同步与正文预取跑完，再占用网络。</summary>
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(20);

    /// <summary>单轮最多查多少个地址（最近往来优先）。</summary>
    private const int MaxLookups = 300;

    /// <summary>并发数：Gravatar 是公开 CDN，3 路并发足够快又不会像扫站。</summary>
    private const int Concurrency = 3;

    /// <summary>
    /// 连续多少次「网络不可用」就放弃本轮：Gravatar 在国内网络经常直接超时，
    /// 300 个地址各等 5 秒会白耗好几分钟，发现打不通就立刻收手。
    /// </summary>
    private const int UnavailableAbortThreshold = 5;

    private readonly ISettingsStore _settings;
    private readonly IAccountRepository _accounts;
    private readonly IContactRepository _contacts;
    private readonly IAvatarService _avatars;
    private readonly AvatarStore _store;
    private readonly ILogger<AvatarLoaderScheduler> _logger;

    /// <summary>本轮连续「网络不可用」次数（命中/未命中会清零）。</summary>
    private int _consecutiveUnavailable;

    /// <summary>本轮是否因网络不可用被提前结束（0 / 1）。</summary>
    private int _aborted;

    public AvatarLoaderScheduler(
        ISettingsStore settings,
        IAccountRepository accounts,
        IContactRepository contacts,
        IAvatarService avatars,
        AvatarStore store,
        ILogger<AvatarLoaderScheduler> logger)
    {
        _settings = settings;
        _accounts = accounts;
        _contacts = contacts;
        _avatars = avatars;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartupTrace.Mark("定时器 头像加载 进入");

        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken).ConfigureAwait(false);

            if (!_settings.Current.LoadContactAvatars)
            {
                _logger.LogInformation("联系人头像加载已关闭（设置 → 通用）");
                return;
            }

            var addresses = await CollectAddressesAsync(stoppingToken).ConfigureAwait(false);
            if (addresses.Count == 0)
            {
                _logger.LogInformation("联系人头像：本地还没有可查询的地址");
                return;
            }

            using var gate = new SemaphoreSlim(Concurrency, Concurrency);

            // 本轮专用取消源：网络整段打不通时用它把剩下的地址一次性放弃
            using var roundCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _consecutiveUnavailable = 0;
            _aborted = 0;

            var tasks = new List<Task>(addresses.Count);
            foreach (var address in addresses)
            {
                await gate.WaitAsync(stoppingToken).ConfigureAwait(false);
                tasks.Add(LoadOneAsync(address, gate, roundCts.Token, roundCts));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // 一轮结束集中通知一次，避免每张头像都重刷整个列表
            _store.NotifyChanged();

            if (Volatile.Read(ref _aborted) == 1)
            {
                _logger.LogInformation(
                    "联系人头像加载提前结束（网络取不到 Gravatar）：{Summary}", _store.Describe());
            }
            else
            {
                _logger.LogInformation("联系人头像加载完成：{Summary}", _store.Describe());
            }
        }
        catch (OperationCanceledException)
        {
            // 程序退出：已拿到的头像留在内存与磁盘缓存里，下次启动直接复用
        }
        catch (Exception ex)
        {
            // 头像只是装饰，任何异常都不影响主流程，也不再重试
            _logger.LogWarning(ex, "联系人头像加载失败");
        }
    }

    private async Task LoadOneAsync(
        string address,
        SemaphoreSlim gate,
        CancellationToken ct,
        CancellationTokenSource roundCts)
    {
        try
        {
            if (ct.IsCancellationRequested) return;
            if (_store.HasAvatar(address)) return;

            _store.CountQuery();
            var result = await _avatars.GetAsync(address, ct).ConfigureAwait(false);
            _store.Record(address, result);

            // 连续失败说明整条网络打不通（国内访问 Gravatar 常超时）：达到阈值就放弃本轮
            if (result.Status == AvatarLookupStatus.Unavailable)
            {
                if (Interlocked.Increment(ref _consecutiveUnavailable) >= UnavailableAbortThreshold
                    && Interlocked.Exchange(ref _aborted, 1) == 0)
                {
                    roundCts.Cancel();
                }
            }
            else
            {
                Interlocked.Exchange(ref _consecutiveUnavailable, 0);
            }
        }
        catch (OperationCanceledException)
        {
            // 本轮被取消（网络不可用提前收手 / 程序退出）：静默收尾
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "头像查询异常：{Email}", address);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 取要查询的地址：各账户自身地址 + 最近往来的联系人（按使用次数、最近使用时间排序）。
    /// 去重后截到 <see cref="MaxLookups"/>，避免一次跑几千个请求。
    /// </summary>
    private async Task<IReadOnlyList<string>> CollectAddressesAsync(CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Add(string? email)
        {
            if (!GravatarAvatarService.TryNormalizeEmail(email, out var normalized)) return;
            if (!seen.Add(normalized)) return;
            result.Add(normalized);
        }

        try
        {
            foreach (var account in await _accounts.GetAllAsync(ct).ConfigureAwait(false))
            {
                Add(account.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取账户列表失败（头像）");
        }

        try
        {
            var contacts = await _contacts.GetAllAsync(ct: ct).ConfigureAwait(false);
            foreach (var contact in contacts
                         .OrderByDescending(c => c.UseCount)
                         .ThenByDescending(c => c.LastUsedUtc))
            {
                if (result.Count >= MaxLookups) break;
                Add(contact.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取联系人失败（头像）");
        }

        return result.Count > MaxLookups ? result.GetRange(0, MaxLookups) : result;
    }
}
