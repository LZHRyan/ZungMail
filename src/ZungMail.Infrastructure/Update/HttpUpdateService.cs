using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZungMail.Core.Abstractions;
using ZungMail.Update.Protocol;
using Microsoft.Extensions.Logging;

namespace ZungMail.Infrastructure.Update;

/// <summary>
/// HTTP 升级客户端：按 <see cref="UpdateRoutes"/> 的固定约定访问升级服务。
/// 所有网络异常都转换成 <see cref="UpdateCheckResult"/> / <see cref="UpdateDownloadResult"/>，
/// 调用方（启动后自动检测）不需要 try/catch。
/// </summary>
public sealed class HttpUpdateService : IUpdateService, IDisposable
{
    private const int MaxMessageLength = 400;
    private static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    private static readonly string[] AllowedExtensions = [".exe", ".msi", ".zip"];

    private readonly ISettingsStore _settings;
    private readonly UpdateAddressResolver _addressResolver;
    private readonly IApplicationVersion _applicationVersion;
    private readonly ILogger<HttpUpdateService> _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _checkTimeout;

    public HttpUpdateService(
        ISettingsStore settings,
        UpdateAddressResolver addressResolver,
        IApplicationVersion applicationVersion,
        ILogger<HttpUpdateService> logger,
        HttpClient? httpClient = null,
        string? downloadsDirectory = null,
        TimeSpan? checkTimeout = null)
    {
        _settings = settings;
        _addressResolver = addressResolver;
        _applicationVersion = applicationVersion;
        _logger = logger;
        _checkTimeout = checkTimeout ?? DefaultCheckTimeout;

        DownloadsDirectory = downloadsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZungMail",
            "updates");

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        if (_ownsHttpClient)
        {
            _http.Timeout = DownloadTimeout;
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"ZungMail/{_applicationVersion.Version} (Windows; update-protocol/{UpdateRoutes.ProtocolVersion})");
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public string DownloadsDirectory { get; }

    public UpdateAddressResolution ResolveAddress() => _addressResolver.Resolve(_settings.Current);

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var resolution = _addressResolver.Resolve(_settings.Current);
        if (!resolution.Success)
        {
            // 没有任何地址来源是「未配置」；写了但解析失败是「地址不合法」，两者界面提示不同
            var status = resolution.Source == UpdateAddressSource.Missing
                ? UpdateCheckStatus.NotConfigured
                : UpdateCheckStatus.InvalidAddress;
            return UpdateCheckResult.Fail(status, resolution.Message);
        }

        var address = resolution.Address!;
        var channel = _addressResolver.ResolveChannel(_settings.Current, resolution.Source);
        var current = _applicationVersion.Version;
        var url = UpdateRoutes.BuildCheckUrl(address, current, channel);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_checkTimeout);

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorAsync(response, timeout.Token).ConfigureAwait(false);
                return UpdateCheckResult.Fail(
                    UpdateCheckStatus.ServerError,
                    $"升级服务返回 HTTP {(int)response.StatusCode}{detail}",
                    address);
            }

            UpdateCheckResponse? payload;
            await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                payload = await JsonSerializer.DeserializeAsync<UpdateCheckResponse>(
                    stream, UpdateJson.Options, timeout.Token).ConfigureAwait(false);
            }

            if (payload is null)
            {
                return UpdateCheckResult.Fail(UpdateCheckStatus.ServerError, "升级服务返回了空响应。", address);
            }

            if (payload.Protocol > UpdateRoutes.ProtocolVersion)
            {
                return UpdateCheckResult.Fail(
                    UpdateCheckStatus.ServerError,
                    $"升级服务协议版本（{payload.Protocol}）高于客户端支持的版本（{UpdateRoutes.ProtocolVersion}），请先升级客户端。",
                    address);
            }

            if (payload.Update is null || payload.Update.Version.Length == 0)
            {
                _logger.LogInformation("升级检测：已是最新版本（本地 {Current}，渠道 {Channel}）", current, channel);
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpToDate,
                    "已是最新版本",
                    null,
                    address,
                    DateTimeOffset.Now);
            }

            var release = payload.Update;
            if (!UpdateVersion.TryParse(release.Version, out var remote))
            {
                return UpdateCheckResult.Fail(
                    UpdateCheckStatus.ServerError,
                    $"升级服务返回的版本号「{release.Version}」无法解析。",
                    address);
            }

            if (!UpdateVersion.TryParse(current, out var local))
            {
                return UpdateCheckResult.Fail(
                    UpdateCheckStatus.ServerError,
                    $"本机版本号「{current}」无法解析，已跳过本次升级检测。",
                    address);
            }

            if (remote <= local)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpToDate,
                    "已是最新版本",
                    null,
                    address,
                    DateTimeOffset.Now);
            }

            _logger.LogInformation("升级检测：发现新版本 {Version}（本地 {Current}）", release.Version, current);
            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                $"发现新版本 v{remote}",
                release,
                address,
                DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return UpdateCheckResult.Fail(
                UpdateCheckStatus.Unreachable,
                $"连接升级服务超时（{_checkTimeout.TotalSeconds:0} 秒），请检查地址或网络。",
                address);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "升级检测失败：{Address}", address);
            return UpdateCheckResult.Fail(
                UpdateCheckStatus.Unreachable,
                "无法连接升级服务：" + Shorten(ex.Message),
                address);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "升级服务响应无法解析：{Address}", address);
            return UpdateCheckResult.Fail(
                UpdateCheckStatus.ServerError,
                "升级服务返回的内容不是有效的升级信息（可能地址指向了其它服务）。",
                address);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "升级检测异常：{Address}", address);
            return UpdateCheckResult.Fail(
                UpdateCheckStatus.ServerError,
                "升级检测失败：" + Shorten(ex.Message),
                address);
        }
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!UpdateVersion.TryParse(release.Version, out var version))
        {
            return UpdateDownloadResult.Fail("升级服务返回的版本号无法解析，已取消下载。");
        }

        if (string.IsNullOrWhiteSpace(release.Sha256) || release.Sha256.Length != 64
                                                      || !release.Sha256.All(Uri.IsHexDigit))
        {
            return UpdateDownloadResult.Fail("升级服务没有提供有效的 SHA256 校验值，已拒绝下载。");
        }

        if (release.Size <= 0)
        {
            return UpdateDownloadResult.Fail("升级服务没有提供安装包大小，已拒绝下载。");
        }

        var resolution = _addressResolver.Resolve(_settings.Current);
        if (!resolution.Success || resolution.Address is null)
        {
            return UpdateDownloadResult.Fail(resolution.Message);
        }

        var uri = UpdateRoutes.ResolveDownloadUri(resolution.Address, release.DownloadUrl);
        if (uri is null)
        {
            return UpdateDownloadResult.Fail("升级服务返回的下载地址不可用。");
        }

        var fileName = BuildSafeFileName(release.FileName);
        if (fileName is null)
        {
            return UpdateDownloadResult.Fail("升级服务返回的安装包文件名不受支持（仅允许 .exe / .msi / .zip）。");
        }

        var targetDirectory = Path.Combine(DownloadsDirectory, version.ToString());
        Directory.CreateDirectory(targetDirectory);
        var finalPath = Path.Combine(targetDirectory, fileName);
        var tempPath = finalPath + ".part";

        if (File.Exists(finalPath) && new FileInfo(finalPath).Length == release.Size
                                   && ComputeFileHash(finalPath) == release.Sha256.ToLowerInvariant())
        {
            progress?.Report(new UpdateDownloadProgress(release.Size, release.Size));
            return UpdateDownloadResult.Ok(finalPath, reused: true);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateDownloadResult.Fail($"下载安装包失败：升级服务返回 HTTP {(int)response.StatusCode}。");
            }

            var total = response.Content.Headers.ContentLength;
            if (total is > 0 && total.Value != release.Size)
            {
                return UpdateDownloadResult.Fail(
                    $"安装包大小与版本记录不一致（服务端 {total.Value} 字节，记录 {release.Size} 字节），已取消下载。");
            }

            long written = 0;
            string actualHash;
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(
                             tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                var lastReport = DateTimeOffset.UtcNow;
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (read <= 0) break;

                    written += read;
                    if (written > release.Size)
                    {
                        return UpdateDownloadResult.Fail("安装包比版本记录更大，已取消下载并删除临时文件。");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                    var now = DateTimeOffset.UtcNow;
                    if (now - lastReport >= TimeSpan.FromMilliseconds(120))
                    {
                        progress?.Report(new UpdateDownloadProgress(written, total ?? release.Size));
                        lastReport = now;
                    }
                }

                await output.FlushAsync(ct).ConfigureAwait(false);
                actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (written != release.Size)
            {
                TryDelete(tempPath);
                return UpdateDownloadResult.Fail(
                    $"安装包下载不完整（{written}/{release.Size} 字节），请重试。");
            }

            if (!string.Equals(actualHash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tempPath);
                _logger.LogWarning(
                    "安装包校验失败：期望 {Expected}，实际 {Actual}", release.Sha256, actualHash);
                return UpdateDownloadResult.Fail("安装包校验失败（SHA256 不一致），已删除下载文件，请重新下载。");
            }

            File.Move(tempPath, finalPath, overwrite: true);
            progress?.Report(new UpdateDownloadProgress(release.Size, release.Size));
            _logger.LogInformation("安装包已下载并通过校验：{Path}", finalPath);
            return UpdateDownloadResult.Ok(finalPath, reused: false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            _logger.LogError(ex, "下载安装包失败：{Uri}", uri);
            return UpdateDownloadResult.Fail("下载安装包失败：" + Shorten(ex.Message));
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return "";
            if (text.Length > MaxMessageLength) text = text[..MaxMessageLength];

            // 服务端错误统一是 {"error":"..."}；其它内容只取首行，避免把整页 HTML 塞进界面
            try
            {
                var payload = JsonSerializer.Deserialize<UpdateErrorResponse>(text, UpdateJson.Options);
                if (!string.IsNullOrWhiteSpace(payload?.Error)) return "：" + payload.Error;
            }
            catch (JsonException)
            {
                // 不是 JSON 就按纯文本截断
            }

            var line = text.Split('\n')[0].Trim();
            return line.Length == 0 ? "" : "：" + line;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string? BuildSafeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Trim());
        if (name.Length == 0) return null;

        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension)) return null;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var result = builder.ToString().Trim();
        if (result.Length == 0) return null;
        if (result.Length > 160)
        {
            // 截断时保留扩展名，否则后续安装器识别会失败
            var keep = Math.Max(1, 160 - extension.Length);
            var baseName = Path.GetFileNameWithoutExtension(result);
            result = baseName[..Math.Min(baseName.Length, keep)] + extension;
        }

        return result;
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 临时文件删不掉不影响本次结果，下次下载会覆盖
        }
    }

    private static string Shorten(string message)
        => message.Length <= MaxMessageLength ? message : message[..MaxMessageLength] + "…";
}
