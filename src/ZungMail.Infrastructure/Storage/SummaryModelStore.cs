using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// 摘要模型仓库：文件放在可丢弃的缓存目录（默认 <c>%LOCALAPPDATA%\ZungMail\models</c>），
/// 支持断点续传、SHA256 校验、删除与「手动放置后重新检测」。
/// <para>
/// 模型不进安装包：一是 400 MB 不该由所有用户承担，二是升级客户端时不必重下模型。
/// </para>
/// </summary>
public sealed class SummaryModelStore : ISummaryModelStore
{
    private const int BufferSize = 128 * 1024;

    private readonly ISettingsStore _settings;
    private readonly HttpClient _http;
    private readonly ILogger<SummaryModelStore>? _logger;

    private readonly object _verifyLock = new();
    private string? _verifiedPath;
    private long _verifiedLength;
    private DateTime _verifiedStamp;
    private bool _verified;
    private string? _verifiedDetail;

    /// <param name="baseDirectory">模型根目录；为空时用 <c>%LOCALAPPDATA%\ZungMail\models</c>（测试可注入临时目录）。</param>
    public SummaryModelStore(
        ISettingsStore settings,
        string? baseDirectory = null,
        ILogger<SummaryModelStore>? logger = null,
        HttpClient? httpClient = null)
    {
        _settings = settings;
        _logger = logger;

        ModelDirectory = Path.Combine(
            baseDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZungMail"),
            "models");

        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
    }

    public string ModelDirectory { get; }

    public string ModelFilePath => Path.Combine(
        ModelDirectory, MailSummarySettings.DefaultModelId, MailSummarySettings.DefaultModelFileName);

    public SummaryModelStatus GetStatus()
    {
        var path = ModelFilePath;
        if (!File.Exists(path))
        {
            return new SummaryModelStatus(SummaryModelState.Missing, MailSummarySettings.DefaultModelId, path, 0, null);
        }

        var size = new FileInfo(path).Length;
        var (ok, detail) = VerifyFile(path, size);

        return new SummaryModelStatus(
            ok ? SummaryModelState.Ready : SummaryModelState.Invalid,
            MailSummarySettings.DefaultModelId,
            path,
            size,
            ok ? null : detail);
    }

    public async Task<SummaryModelStatus> DownloadAsync(
        IProgress<MailSummaryProgress>? progress = null,
        CancellationToken ct = default)
    {
        var url = MailSummarySettings.ResolveModelUrl(_settings.Current.MailSummaryModelUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "还没有配置模型下载地址：请在 设置 → 本地 AI 助手 里填写模型 URL，" +
                "或把 GGUF 文件放进模型目录后点「重新检测」。");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("模型地址必须是 http/https 链接：" + url);
        }

        var target = ModelFilePath;
        var part = target + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // 已有完整文件且校验通过：直接返回就绪，不重复下载
        if (GetStatus() is { State: SummaryModelState.Ready } ready) return ready;

        var resumeFrom = File.Exists(part) ? new FileInfo(part).Length : 0;

        progress?.Report(new MailSummaryProgress("正在下载模型", 0, resumeFrom > 0
            ? $"从 {resumeFrom / 1024 / 1024} MB 处续传…"
            : "准备下载…"));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && resumeFrom > 0)
        {
            // 服务器不认续传：删掉半截文件，让用户重来一次
            TryDelete(part);
            throw new IOException("服务器不支持续传，请重新开始下载。");
        }

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength is { } length ? length + resumeFrom : (long?)null;
        var append = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = new FileStream(
                         part,
                         append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read,
                         BufferSize,
                         useAsync: true))
        {
            var buffer = new byte[BufferSize];
            var received = append ? resumeFrom : 0;
            long lastReport = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                // 每 4 MB 报一次进度，避免高频回调把 UI 线程刷爆
                if (received - lastReport < 4L * 1024 * 1024) continue;
                lastReport = received;
                var fraction = total is > 0 ? (double)received / total.Value : (double?)null;
                progress?.Report(new MailSummaryProgress(
                    "正在下载模型",
                    fraction,
                    $"{received / 1024.0 / 1024.0:F0} MB" +
                    (total is > 0 ? $" / {total.Value / 1024.0 / 1024.0:F0} MB" : "")));
            }
        }

        progress?.Report(new MailSummaryProgress("正在校验模型", 0.95, "计算 SHA256…"));

        var partSize = new FileInfo(part).Length;
        var (ok, detail) = VerifyFile(part, partSize, force: true);
        if (!ok)
        {
            TryDelete(part);
            throw new InvalidDataException("模型校验失败：" + (detail ?? "SHA256 不匹配") + "，已删除下载文件。");
        }

        File.Move(part, target, overwrite: true);
        InvalidateVerification();

        progress?.Report(new MailSummaryProgress("完成", 1, "模型已就绪"));
        _logger?.LogInformation("摘要模型下载完成：{Path}（{Size} MB）", target, partSize / 1024 / 1024);

        return GetStatus();
    }

    public void Delete()
    {
        TryDelete(ModelFilePath);
        TryDelete(ModelFilePath + ".part");
        InvalidateVerification();
        _logger?.LogInformation("已删除摘要模型：{Path}", ModelFilePath);
    }

    public SummaryModelStatus Recheck()
    {
        InvalidateVerification();
        return GetStatus();
    }

    /// <summary>校验（按 路径+长度+修改时间 缓存结果，避免每次状态查询都算 400 MB 的哈希）。</summary>
    private (bool Ok, string? Detail) VerifyFile(string path, long size, bool force = false)
    {
        var sha = MailSummarySettings.ResolveModelSha256(_settings.Current.MailSummaryModelSha256);
        var stamp = File.GetLastWriteTimeUtc(path);

        lock (_verifyLock)
        {
            if (!force &&
                _verifiedPath == path &&
                _verifiedLength == size &&
                _verifiedStamp == stamp &&
                _verifiedDetail is not null)
            {
                return (_verified, _verifiedDetail);
            }
        }

        var (ok, detail) = VerifyCore(path, size, sha);

        lock (_verifyLock)
        {
            _verifiedPath = path;
            _verifiedLength = size;
            _verifiedStamp = stamp;
            _verified = ok;
            _verifiedDetail = detail ?? "ok";
        }

        return (ok, detail);
    }

    private static (bool Ok, string? Detail) VerifyCore(string path, long size, string sha)
    {
        if (size <= 0) return (false, "模型文件为空");

        // 没配置哈希时只做基本合理性检查
        if (string.IsNullOrWhiteSpace(sha))
        {
            return size > 1024 * 1024
                ? (true, null)
                : (false, "文件过小，可能不是完整的 GGUF 模型");
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return string.Equals(actual, sha.Trim().ToLowerInvariant(), StringComparison.Ordinal)
            ? (true, null)
            : (false, "SHA256 校验不匹配");
    }

    private void InvalidateVerification()
    {
        lock (_verifyLock)
        {
            _verifiedPath = null;
            _verifiedDetail = null;
            _verified = false;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "删除模型文件失败：{Path}", path);
        }
    }
}
