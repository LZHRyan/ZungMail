using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.Application.Services;

/// <summary>新增/修改发件人名单的结果，供界面直接展示提示文案。</summary>
public sealed record SenderFilterSaveResult(bool Success, string Message, SenderFilter? Filter);

/// <summary>
/// 本地发件人黑白名单：入库时判定发件人，设置页负责维护。
/// 结果缓存 30 秒，写入后立即失效，避免每封新邮件都查库；
/// 判定只影响本地（垃圾邮件目录 + 待同步队列），不改写服务器端的垃圾标记。
/// </summary>
public sealed class SenderFilterService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly ISenderFilterRepository _repository;
    private readonly ILogger<SenderFilterService> _logger;
    private readonly object _gate = new();
    private IReadOnlyList<SenderFilter>? _cache;
    private DateTimeOffset _cachedAt;

    public SenderFilterService(ISenderFilterRepository repository, ILogger<SenderFilterService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>读取全部条目；<paramref name="forceRefresh"/> 为 true 时忽略缓存。</summary>
    public async Task<IReadOnlyList<SenderFilter>> GetAllAsync(
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        if (!forceRefresh && TryGetCached() is { } cached) return cached;

        var filters = await _repository.GetAllAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _cache = filters;
            _cachedAt = DateTimeOffset.UtcNow;
        }

        return filters;
    }

    /// <summary>归一化并保存一条名单；非法输入返回失败说明而不是抛异常。</summary>
    public async Task<SenderFilterSaveResult> SaveAsync(
        string? pattern,
        bool isBlacklist,
        string? note = null,
        CancellationToken ct = default)
    {
        var normalized = SenderFilterMatch.Normalize(pattern);
        if (normalized is null)
        {
            return new SenderFilterSaveResult(
                false,
                "请输入完整邮箱地址（boss@example.com）或域名（@example.com）",
                null);
        }

        var filter = new SenderFilter
        {
            Pattern = normalized,
            IsBlacklist = isBlacklist,
            Note = note?.Trim() ?? "",
            CreatedUtc = DateTimeOffset.UtcNow
        };

        try
        {
            await _repository.UpsertAsync(filter, ct).ConfigureAwait(false);
            Invalidate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存发件人名单 {Pattern} 失败", normalized);
            return new SenderFilterSaveResult(false, "保存失败：" + ex.Message, null);
        }

        return new SenderFilterSaveResult(
            true,
            $"已加入{(isBlacklist ? "黑名单" : "白名单")}：{normalized}",
            filter);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        try
        {
            var deleted = await _repository.DeleteAsync(id, ct).ConfigureAwait(false);
            if (deleted) Invalidate();
            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除发件人名单 {Id} 失败", id);
            return false;
        }
    }

    /// <summary>判定发件人是否命中名单；白名单优先于黑名单，判定失败按「未命中」处理。</summary>
    public async Task<SenderVerdict> ClassifyAsync(string? address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address)) return SenderVerdict.None;

        try
        {
            var filters = await GetAllAsync(ct: ct).ConfigureAwait(false);
            return SenderFilterMatch.Classify(filters, address);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取发件人名单失败，按未命中处理");
            return SenderVerdict.None;
        }
    }

    /// <summary>丢弃缓存，下一次判定重新读库。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
            _cachedAt = default;
        }
    }

    private IReadOnlyList<SenderFilter>? TryGetCached()
    {
        lock (_gate)
        {
            if (_cache is null) return null;
            return DateTimeOffset.UtcNow - _cachedAt <= CacheTtl ? _cache : null;
        }
    }
}
