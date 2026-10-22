using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ZungMail.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace ZungMail.Infrastructure.Avatars;

/// <summary>
/// Gravatar 头像来源：按 Gravatar 规范把「去空格 + 小写」的邮箱做 MD5，
/// 请求 <c>https://www.gravatar.com/avatar/{hash}?d=404&amp;s=96</c>：
/// <list type="bullet">
/// <item>200 → 图片落盘缓存，下次直接用；</item>
/// <item>404 → 写 <c>.miss</c> 标记，7 天内不再重复问；</item>
/// <item>超时 / 网络异常 / 5xx → 什么都不记，下次运行再试。</item>
/// </list>
/// 任何失败都只返回空结果，绝不抛异常——头像只是装饰，不能影响收信与界面。
/// </summary>
public sealed class GravatarAvatarService : IAvatarService, IDisposable
{
    /// <summary>单次请求超时：头像用 96px，正常几十毫秒，5 秒足够判定网络不可用。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>「服务器没有这个头像」的有效期：7 天内不再为同一个地址发请求。</summary>
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromDays(7);

    /// <summary>头像上限：96px 的图正常 < 100 KB，超过这个尺寸直接当异常丢弃。</summary>
    private const int MaxImageBytes = 512 * 1024;

    private readonly string _directory;
    private readonly ILogger<GravatarAvatarService> _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public GravatarAvatarService(
        IAppPaths paths,
        ILogger<GravatarAvatarService> logger,
        HttpMessageHandler? handler = null)
    {
        // 不往 IAppPaths 上加成员（会牵动一堆测试替身），头像目录就近派生自数据根目录
        _directory = Path.Combine(paths.RootDirectory, "avatars");
        _logger = logger;
        _ownsHttpClient = handler is null;
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip }) { Timeout = RequestTimeout }
            : new HttpClient(handler) { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ZungMail/1.1 (avatar; gravatar)");
    }

    /// <summary>本地头像缓存目录（图片 <c>{hash}.bin</c>，未命中标记 <c>{hash}.miss</c>）。</summary>
    public string CacheDirectory => _directory;

    /// <summary>Gravatar 官方算法：邮箱 trim 后转小写，再取 MD5 的十六进制小写。</summary>
    public static string HashEmail(string email)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>邮箱归一化：去空格、小写；没有 @ 的字符串不算邮箱。</summary>
    public static bool TryNormalizeEmail(string? email, out string normalized)
    {
        normalized = (email ?? "").Trim().ToLowerInvariant();
        return normalized.Length >= 3 && normalized.Contains('@') && !normalized.Contains(' ');
    }

    public async Task<AvatarLookupResult> GetAsync(string? email, CancellationToken ct = default)
    {
        if (!TryNormalizeEmail(email, out var normalized)) return AvatarLookupResult.NotFound;

        var hash = HashEmail(normalized);
        var imagePath = Path.Combine(_directory, hash + ".bin");
        var missPath = Path.Combine(_directory, hash + ".miss");

        try
        {
            if (File.Exists(imagePath))
            {
                var cached = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
                if (cached.Length > 0) return new AvatarLookupResult(cached, AvatarLookupStatus.Cached);
            }

            if (IsMissFresh(missPath)) return AvatarLookupResult.NotFound;

            var url = string.Create(CultureInfo.InvariantCulture, $"https://www.gravatar.com/avatar/{hash}?d=404&s=96");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                WriteMiss(missPath);
                return AvatarLookupResult.NotFound;
            }

            if (!response.IsSuccessStatusCode)
            {
                // 5xx / 429 之类：不记 miss，下次运行再试
                _logger.LogDebug("Gravatar 返回 HTTP {Status}：{Email}", (int)response.StatusCode, normalized);
                return AvatarLookupResult.Unavailable;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
            {
                _logger.LogDebug("Gravatar 响应体积异常（{Bytes} 字节）：{Email}", bytes.Length, normalized);
                return AvatarLookupResult.Unavailable;
            }

            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(imagePath, bytes, ct).ConfigureAwait(false);
            TryDelete(missPath);
            return new AvatarLookupResult(bytes, AvatarLookupStatus.Hit);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 单次超时：当作暂时不可用，不写 miss
            return AvatarLookupResult.Unavailable;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gravatar 查询失败：{Email}", normalized);
            return AvatarLookupResult.Unavailable;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    private static bool IsMissFresh(string missPath)
    {
        try
        {
            if (!File.Exists(missPath)) return false;
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(missPath) < NotFoundTtl;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void WriteMiss(string missPath)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(missPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "写入头像未命中标记失败：{Path}", missPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 标记删不掉不影响结果，下次命中会再覆盖
        }
    }
}
