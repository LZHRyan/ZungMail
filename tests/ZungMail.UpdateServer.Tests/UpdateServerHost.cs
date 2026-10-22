using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ZungMail.Update.Protocol;
using ZungMail.UpdateServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ZungMail.UpdateServer.Tests;

/// <summary>
/// 每个测试起一个真实 Kestrel（端口 0）+ 临时数据目录，请求走完整的 HTTP 栈，
/// 覆盖登录、上传、发布、检测、下载与重启后持久化，而不是直接调服务类。
/// </summary>
public sealed class UpdateServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _dataDirectory;

    private UpdateServerHost(WebApplication app, string dataDirectory, string baseUrl)
    {
        _app = app;
        _dataDirectory = dataDirectory;
        BaseUrl = baseUrl;

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = false
        };
        Client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
    }

    public HttpClient Client { get; }

    public string BaseUrl { get; }

    public string DataDirectory => _dataDirectory;

    public static async Task<UpdateServerHost> StartAsync(
        string? adminPassword = "test-admin-pass-123",
        Action<UpdateServerOptions>? configure = null,
        string? dataDirectory = null)
    {
        var data = dataDirectory ?? Path.Combine(
            Path.GetTempPath(), "mc-update-server-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);

        var contentRoot = Path.Combine(FindRepositoryRoot(), "src", "ZungMail.UpdateServer");
        var app = ServerBootstrap.Build([], options =>
        {
            options.DataDirectory = data;
            options.AdminPassword = adminPassword;
            configure?.Invoke(options);
        }, contentRoot);

        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        var host = new UpdateServerHost(app, data, ResolveAddress(app));
        host.ApplyClientHeader();
        return host;
    }

    /// <summary>重启：复用同一个数据目录验证版本记录与口令哈希都落盘。</summary>
    public static async Task<UpdateServerHost> RestartAsync(UpdateServerHost previous, string? adminPassword = null)
    {
        var data = previous.DataDirectory;
        await previous.StopAsync(deleteData: false);
        return await StartAsync(adminPassword, dataDirectory: data);
    }

    public async Task LoginAsync(string? password = "test-admin-pass-123")
    {
        using var response = await Client.PostAsync(
            "/api/admin/session",
            Json(new { password }));
        response.EnsureSuccessStatusCode();
    }

    public Task<HttpResponseMessage> UploadAsync(
        byte[] bytes,
        string version,
        string channel = "stable",
        string notes = "测试版本",
        bool mandatory = false,
        string? minVersion = null,
        string fileName = "ZungMail-setup.exe")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(version, Encoding.UTF8), "version");
        form.Add(new StringContent(channel, Encoding.UTF8), "channel");
        form.Add(new StringContent(notes, Encoding.UTF8), "notes");
        form.Add(new StringContent(mandatory ? "true" : "false"), "mandatory");
        if (!string.IsNullOrEmpty(minVersion)) form.Add(new StringContent(minVersion, Encoding.UTF8), "minVersion");

        return Client.PostAsync("/api/admin/releases", form);
    }

    public async Task<JsonDocument> GetReleasesAsync()
    {
        using var response = await Client.GetAsync("/api/admin/releases");
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }

    public async Task<UpdateCheckResponse> CheckAsync(string current, string channel = "stable")
    {
        using var response = await Client.GetAsync(
            $"/api/update/check?current={Uri.EscapeDataString(current)}&channel={Uri.EscapeDataString(channel)}&os=windows&arch=x64");
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<UpdateCheckResponse>(text, UpdateJson.Options)!;
    }

    public async ValueTask DisposeAsync() => await StopAsync(deleteData: true);

    public async Task StopAsync(bool deleteData)
    {
        Client.Dispose();
        try { await _app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* 已停止或超时 */ }
        await _app.DisposeAsync();

        if (!deleteData) return;
        try { Directory.Delete(_dataDirectory, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private void ApplyClientHeader()
        => Client.DefaultRequestHeaders.UserAgent.ParseAdd("ZungMail.UpdateServer.Tests/1.0");

    private static string ResolveAddress(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var feature = server.Features.Get<IServerAddressesFeature>();
        var address = feature?.Addresses.FirstOrDefault();
        if (string.IsNullOrEmpty(address)) throw new InvalidOperationException("服务未绑定监听地址。");
        return address.Replace("http://[::]", "http://127.0.0.1").TrimEnd('/');
    }

    private static StringContent Json(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ZungMail.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（ZungMail.sln）。");
    }
}
