using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Update;
using ZungMail.Update.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 客户端升级链路：地址来源优先级、检测结果分类、下载进度、SHA256 校验与取消。
/// 用本机 TcpListener 模拟升级服务，不依赖外网。
/// </summary>
public sealed class UpdateClientServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mc-update-client-tests", Guid.NewGuid().ToString("N"));

    public UpdateClientServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试目录清理失败不影响结论 */ }
    }

    // ---------------- 地址解析 ----------------

    [Fact]
    public void Resolver_UsesUserSettingFirst()
    {
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "update.json"), """{"updateServiceUrl":"file.example.com"}""");

        var resolver = new UpdateAddressResolver(install);
        var settings = new AppSettings { UpdateServiceUrl = "user.example.com:9000" };

        var result = resolver.Resolve(settings);

        Assert.True(result.Success);
        Assert.Equal(UpdateAddressSource.UserSetting, result.Source);
        Assert.Equal("http://user.example.com:9000/", result.Address!.ToString());
    }

    [Fact]
    public void Resolver_FallsBackToInstallFile()
    {
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(
            Path.Combine(install, "update.json"),
            """{"updateServiceUrl":"updates.example.com:8080","channel":"beta"}""");

        var resolver = new UpdateAddressResolver(install);
        var settings = new AppSettings();

        var result = resolver.Resolve(settings);

        Assert.True(result.Success);
        Assert.Equal(UpdateAddressSource.InstallFile, result.Source);
        Assert.Equal("http://updates.example.com:8080/", result.Address!.ToString());
        Assert.Equal("beta", resolver.ResolveChannel(settings, result.Source));
    }

    [Fact]
    public void Resolver_ReportsMissingWhenNothingConfigured()
    {
        var install = Path.Combine(_root, "empty-install");
        Directory.CreateDirectory(install);

        var result = new UpdateAddressResolver(install).Resolve(new AppSettings());

        Assert.False(result.Success);
        Assert.Equal(UpdateAddressSource.Missing, result.Source);
        Assert.Contains("未配置", result.Message);
    }

    [Fact]
    public void Resolver_ReportsInvalidUserSetting()
    {
        var result = new UpdateAddressResolver(_root).Resolve(new AppSettings { UpdateServiceUrl = "ftp://x" });

        Assert.False(result.Success);
        Assert.Equal(UpdateAddressSource.UserSetting, result.Source);
        Assert.Contains("http", result.Message);
    }

    [Fact]
    public void Resolver_IgnoresBrokenInstallFile()
    {
        var install = Path.Combine(_root, "broken");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "update.json"), "{ not json");

        var result = new UpdateAddressResolver(install).Resolve(new AppSettings());

        Assert.False(result.Success);
        Assert.Equal(UpdateAddressSource.InstallFile, result.Source);
        Assert.Contains("update.json", result.Message);
    }

    [Fact]
    public void Settings_DeserializeOldFileWithDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}", new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;

        Assert.Equal("", settings.UpdateServiceUrl);
        Assert.Equal("stable", settings.UpdateChannel);
        Assert.True(settings.UpdateAutoCheckEnabled);
        Assert.Equal(3, settings.UpdateAutoCheckDelayMinutes);
        Assert.Equal(3, AppSettings.EffectiveUpdateDelayMinutes(0));
        Assert.Equal(30, AppSettings.EffectiveUpdateDelayMinutes(999));
    }

    // ---------------- 检测 ----------------

    [Fact]
    public async Task Check_ReturnsUpdateAvailableForHigherVersion()
    {
        await using var server = await TestHttpServer.StartAsync(request =>
        {
            if (request.Path.StartsWith("/api/update/check")) return TestHttpServer.Json(CheckPayload("1.1.0"));
            return TestHttpServer.NotFound();
        });

        using var service = CreateService(server.BaseUrl);
        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.1.0", result.Release!.Version);
        Assert.Equal("修复若干问题", result.Release.Notes);
        Assert.Contains("current=1.0.0", server.Requests[0]);
        Assert.Contains("channel=stable", server.Requests[0]);
    }

    [Fact]
    public async Task Check_TreatsEqualOrLowerVersionAsUpToDate()
    {
        await using var server = await TestHttpServer.StartAsync(request =>
            request.Path.StartsWith("/api/update/check")
                ? TestHttpServer.Json(CheckPayload("1.0.0"))
                : TestHttpServer.NotFound());

        using var service = CreateService(server.BaseUrl);
        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task Check_ReturnsUpToDateForNullUpdate()
    {
        await using var server = await TestHttpServer.StartAsync(request =>
            request.Path.StartsWith("/api/update/check")
                ? TestHttpServer.Json(new UpdateCheckResponse { ServerTime = DateTimeOffset.UtcNow, Update = null })
                : TestHttpServer.NotFound());

        using var service = CreateService(server.BaseUrl);
        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal("已是最新版本", result.Message);
    }

    [Fact]
    public async Task Check_ClassifiesServerError()
    {
        await using var server = await TestHttpServer.StartAsync(_ =>
            TestHttpServer.Json(new UpdateErrorResponse { Error = "参数缺失" }, statusCode: 500));

        using var service = CreateService(server.BaseUrl);
        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckStatus.ServerError, result.Status);
        Assert.Contains("500", result.Message);
        Assert.Contains("参数缺失", result.Message);
    }

    [Fact]
    public async Task Check_ClassifiesNonJsonResponse()
    {
        await using var server = await TestHttpServer.StartAsync(_ => TestHttpServer.Html("<html>hello</html>"));

        using var service = CreateService(server.BaseUrl);
        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckStatus.ServerError, result.Status);
        Assert.Contains("不是有效的升级信息", result.Message);
    }

    [Fact]
    public async Task Check_ClassifiesUnreachable()
    {
        // 先起再停，拿到一个当前没有监听的端口
        var probe = new TestHttpServer();
        var port = probe.Port;
        await probe.DisposeAsync();

        using var service = CreateService($"http://127.0.0.1:{port}");
        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Unreachable, result.Status);
        Assert.Contains("无法连接升级服务", result.Message);
    }

    [Fact]
    public async Task Check_TimesOut()
    {
        await using var server = await TestHttpServer.StartAsync(async (_, stream) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            await TestHttpServer.WriteAsync(stream, 200, "application/json", "{}");
        }, start: false);

        using var service = CreateService(server.BaseUrl, checkTimeout: TimeSpan.FromMilliseconds(300));
        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Unreachable, result.Status);
        Assert.Contains("超时", result.Message);
    }

    [Fact]
    public async Task Check_DoesNotRequestWhenNotConfigured()
    {
        await using var server = await TestHttpServer.StartAsync(_ => TestHttpServer.Json(CheckPayload("9.9.9")));

        var install = Path.Combine(_root, "no-config");
        Directory.CreateDirectory(install);
        using var service = new HttpUpdateService(
            new StubSettingsStore(new AppSettings()),
            new UpdateAddressResolver(install),
            new FixedVersion("1.0.0"),
            NullLogger<HttpUpdateService>.Instance,
            downloadsDirectory: Path.Combine(_root, "downloads"));

        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckStatus.NotConfigured, result.Status);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Check_ClassifiesInvalidAddress()
    {
        using var service = CreateService("ftp://updates.example.com");
        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckStatus.InvalidAddress, result.Status);
        Assert.Contains("http", result.Message);
    }

    // ---------------- 下载 ----------------

    [Fact]
    public async Task Download_SavesVerifiedFileWithProgress()
    {
        var payload = CreatePayload(256 * 1024);
        var sha = Sha256(payload);

        await using var server = await TestHttpServer.StartAsync(request =>
            request.Path.StartsWith("/api/update/download")
                ? TestHttpServer.Bytes(payload)
                : TestHttpServer.NotFound());

        using var service = CreateService(server.BaseUrl);
        var progress = new RecordingProgress();
        var result = await service.DownloadAsync(BuildRelease("1.1.0", payload, sha), progress);

        Assert.True(result.Success, result.Message);
        Assert.False(result.Reused);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.Contains(progress.Values, p => p.BytesReceived > 0 && p.Percent > 0);
        Assert.Contains(Path.Combine("1.1.0", "ZungMail-1.1.0-win-x64-setup.exe"), result.FilePath);
    }

    [Fact]
    public async Task Download_RejectsHashMismatchAndDeletesFile()
    {
        var payload = CreatePayload(64 * 1024);
        var wrongHash = new string('a', 64);

        await using var server = await TestHttpServer.StartAsync(_ => TestHttpServer.Bytes(payload));

        using var service = CreateService(server.BaseUrl);
        var result = await service.DownloadAsync(BuildRelease("1.1.0", payload, wrongHash));

        Assert.False(result.Success);
        Assert.Contains("校验失败", result.Message);
        var directory = Path.Combine(service.DownloadsDirectory, "1.1.0");
        Assert.False(Directory.Exists(directory) && Directory.GetFiles(directory).Length > 0);
    }

    [Fact]
    public async Task Download_ReusesExistingVerifiedFile()
    {
        var payload = CreatePayload(32 * 1024);
        var sha = Sha256(payload);

        await using var server = await TestHttpServer.StartAsync(_ => TestHttpServer.Bytes(payload));

        using var service = CreateService(server.BaseUrl);
        var first = await service.DownloadAsync(BuildRelease("1.1.0", payload, sha));
        Assert.True(first.Success);

        // 第二次不应再打服务端
        var before = server.Requests.Count;
        var second = await service.DownloadAsync(BuildRelease("1.1.0", payload, sha));

        Assert.True(second.Success);
        Assert.True(second.Reused);
        Assert.Equal(before, server.Requests.Count);
    }

    [Fact]
    public async Task Download_RejectsMissingChecksum()
    {
        using var service = CreateService("http://127.0.0.1:1");
        var release = new UpdateReleaseInfo
        {
            Version = "1.1.0",
            FileName = "setup.exe",
            Size = 10,
            Sha256 = "",
            DownloadUrl = "/api/update/download/x"
        };

        var result = await service.DownloadAsync(release);

        Assert.False(result.Success);
        Assert.Contains("SHA256", result.Message);
    }

    [Fact]
    public async Task Download_RejectsUnsupportedFileName()
    {
        var payload = CreatePayload(1024);
        var release = BuildRelease("1.1.0", payload, Sha256(payload));
        release.FileName = "payload.bat";

        using var service = CreateService("http://127.0.0.1:1");
        var result = await service.DownloadAsync(release);

        Assert.False(result.Success);
        Assert.Contains("文件名不受支持", result.Message);
    }

    [Fact]
    public async Task Download_CanBeCancelled()
    {
        var payload = CreatePayload(512 * 1024);

        await using var server = await TestHttpServer.StartAsync(async (_, stream) =>
        {
            await TestHttpServer.WriteHeadersAsync(stream, 200, "application/octet-stream", payload.Length);
            await stream.WriteAsync(payload.AsMemory(0, 1024));
            await stream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(5));
        });

        using var service = CreateService(server.BaseUrl);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadAsync(BuildRelease("1.1.0", payload, Sha256(payload)), ct: cts.Token));
    }

    // ---------------- 工具 ----------------

    private HttpUpdateService CreateService(string baseUrl, TimeSpan? checkTimeout = null)
    {
        var settings = new StubSettingsStore(new AppSettings { UpdateServiceUrl = baseUrl });
        var install = Path.Combine(_root, "no-config");
        Directory.CreateDirectory(install);

        return new HttpUpdateService(
            settings,
            new UpdateAddressResolver(install),
            new FixedVersion("1.0.0"),
            NullLogger<HttpUpdateService>.Instance,
            downloadsDirectory: Path.Combine(_root, "downloads"),
            checkTimeout: checkTimeout);
    }

    private static UpdateCheckResponse CheckPayload(string version) => new()
    {
        ServerTime = DateTimeOffset.UtcNow,
        Update = new UpdateReleaseInfo
        {
            Id = "stable-" + version,
            Version = version,
            Channel = "stable",
            Notes = "修复若干问题",
            FileName = $"ZungMail-{version}-win-x64-setup.exe",
            Size = 1024,
            Sha256 = new string('b', 64),
            PublishedAt = DateTimeOffset.UtcNow,
            DownloadUrl = "/api/update/download/stable-" + version
        }
    };

    private static UpdateReleaseInfo BuildRelease(string version, byte[] payload, string sha) => new()
    {
        Id = "stable-" + version,
        Version = version,
        Channel = "stable",
        Notes = "测试",
        FileName = $"ZungMail-{version}-win-x64-setup.exe",
        Size = payload.Length,
        Sha256 = sha,
        PublishedAt = DateTimeOffset.UtcNow,
        DownloadUrl = "/api/update/download/stable-" + version
    };

    private static byte[] CreatePayload(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class StubSettingsStore(AppSettings settings) : ISettingsStore
    {
        public AppSettings Current { get; } = settings;

        public string? LoadWarning => null;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedVersion(string version) : IApplicationVersion
    {
        public string Version { get; } = version;
    }

    private sealed class RecordingProgress : IProgress<UpdateDownloadProgress>
    {
        public List<UpdateDownloadProgress> Values { get; } = new();

        public void Report(UpdateDownloadProgress value)
        {
            lock (Values) Values.Add(value);
        }
    }
}

/// <summary>最小 HTTP 服务替身：只处理无 body 的 GET，便于精确控制状态码、延迟与半截响应。</summary>
public sealed class TestHttpServer : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _requests = new();
    private readonly Func<TestHttpRequest, NetworkStream, Task> _handler;
    private Task? _loop;

    private TestHttpServer(Func<TestHttpRequest, NetworkStream, Task> handler, bool start)
    {
        _handler = handler;
        _listener.Start();
        if (start) Start();
    }

    /// <summary>不启动的实例只用于占位取端口（拿到端口后立即释放即得到空闲端口）。</summary>
    public TestHttpServer()
        : this((_, _) => Task.CompletedTask, start: false)
    {
    }

    public static Task<TestHttpServer> StartAsync(
        Func<TestHttpRequest, TestHttpResponse> handler,
        bool start = true)
        => StartAsync((request, stream) => WriteAsync(stream, handler(request)), start);

    public static Task<TestHttpServer> StartAsync(Func<TestHttpRequest, NetworkStream, Task> handler, bool start = true)
        => Task.FromResult(new TestHttpServer(handler, start));

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public IReadOnlyList<string> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public void Start() => _loop ??= Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(System.Net.Sockets.TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                var request = await ReadRequestAsync(stream, _cts.Token);
                if (request is not null) await _handler(request, stream);
            }
            catch (Exception)
            {
                // 客户端主动断开（取消下载）时写入会失败，属预期
            }
        }
    }

    private async Task<TestHttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(1024);
        var single = new byte[1];
        while (buffer.Count < 16384)
        {
            var read = await stream.ReadAsync(single, ct);
            if (read == 0) return null;
            buffer.Add(single[0]);
            if (buffer.Count >= 4 &&
                buffer[^4] == (byte)'\r' && buffer[^3] == (byte)'\n' &&
                buffer[^2] == (byte)'\r' && buffer[^1] == (byte)'\n')
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(buffer.ToArray());
        var firstLine = text.Split("\r\n")[0];
        var parts = firstLine.Split(' ');
        if (parts.Length < 2) return null;

        // 带 body 的请求（反馈提交）：不读完就回响应会让客户端在写 body 时收到 RST
        var body = await ReadBodyAsync(stream, text, ct);

        lock (_requests) _requests.Add(firstLine);
        return new TestHttpRequest(parts[0], parts[1], text, body);
    }

    private static async Task<string> ReadBodyAsync(NetworkStream stream, string headers, CancellationToken ct)
    {
        var length = 0;
        foreach (var line in headers.Split("\r\n"))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            int.TryParse(line["Content-Length:".Length..].Trim(), out length);
            break;
        }

        if (length <= 0) return "";

        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, length - offset), ct);
            if (read == 0) break;
            offset += read;
        }

        return Encoding.UTF8.GetString(body, 0, offset);
    }

    public static async Task WriteAsync(NetworkStream stream, TestHttpResponse response)
        => await WriteAsync(stream, response.StatusCode, response.ContentType, response.Body);

    public static async Task WriteAsync(NetworkStream stream, int status, string contentType, string body)
        => await WriteAsync(stream, status, contentType, Encoding.UTF8.GetBytes(body));

    public static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        var headers = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
                      $"Content-Type: {contentType}\r\n" +
                      $"Content-Length: {body.Length}\r\n" +
                      "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public static async Task WriteHeadersAsync(NetworkStream stream, int status, string contentType, long contentLength)
    {
        var headers = $"HTTP/1.1 {status} OK\r\n" +
                      $"Content-Type: {contentType}\r\n" +
                      $"Content-Length: {contentLength}\r\n" +
                      "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
        await stream.FlushAsync();
    }

    public static TestHttpResponse Json(object payload, int statusCode = 200)
        => new(statusCode, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, UpdateJson.Options));

    public static TestHttpResponse Json(string rawJson, int statusCode = 200)
        => new(statusCode, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(rawJson));

    public static TestHttpResponse Html(string html) => new(200, "text/html", Encoding.UTF8.GetBytes(html));

    public static TestHttpResponse Bytes(byte[] bytes) => new(200, "application/octet-stream", bytes);

    public static TestHttpResponse NotFound() => new(404, "text/plain", Encoding.UTF8.GetBytes("not found"));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* 关闭时忽略 */ }
        }

        _listener.Server.Dispose();
        _cts.Dispose();
    }
}

public sealed record TestHttpRequest(string Method, string PathWithQuery, string RawHeaders, string Body = "")
{
    public string Path => PathWithQuery.Split('?')[0];
}

public sealed record TestHttpResponse(int StatusCode, string ContentType, byte[] Body);
