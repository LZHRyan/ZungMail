using System.Net;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Update;
using ZungMail.Update.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.UpdateServer.Tests;

/// <summary>服务可观测性与端到端契约：健康检查、管理页静态资源、真实客户端链路。</summary>
public sealed class ServerContractTests
{
    [Fact]
    public async Task Health_ReportsWritableDataDirectory()
    {
        await using var host = await UpdateServerHost.StartAsync();

        using var response = await host.Client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"ok\"", body);
        Assert.Contains("\"dataWritable\":true", body);
    }

    [Fact]
    public async Task AdminPage_IsServedFromWwwroot()
    {
        await using var host = await UpdateServerHost.StartAsync();

        using var page = await host.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("text/html", page.Content.Headers.ContentType?.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("ZungMail 升级服务", html);
        Assert.Contains("app.js", html);

        using var script = await host.Client.GetAsync("/app.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("api/admin/releases", await script.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ping_ExposesProtocolVersion()
    {
        await using var host = await UpdateServerHost.StartAsync();

        using var response = await host.Client.GetAsync("/api/update/ping");
        var ping = System.Text.Json.JsonSerializer.Deserialize<UpdatePingResponse>(
            await response.Content.ReadAsStringAsync(), UpdateJson.Options)!;

        Assert.Equal(UpdateRoutes.ProtocolVersion, ping.Protocol);
        Assert.Equal(UpdateRoutes.ServiceName, ping.Service);
    }

    [Fact]
    public async Task RealClient_DetectsDownloadsAndVerifiesInstaller()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();
        var payload = new byte[96 * 1024];
        new Random(7).NextBytes(payload);
        await host.UploadAsync(payload, "2.0.0", notes: "端到端验证");
        await PublishAsync(host, "stable-2.0.0");

        var downloads = Path.Combine(Path.GetTempPath(), "mc-update-e2e", Guid.NewGuid().ToString("N"));
        var settings = new StubSettingsStore(new AppSettings { UpdateServiceUrl = host.BaseUrl });
        using (var client = new HttpUpdateService(
                   settings,
                   new UpdateAddressResolver(Path.Combine(host.DataDirectory, "install")),
                   new FixedVersion("1.0.0"),
                   NullLogger<HttpUpdateService>.Instance,
                   downloadsDirectory: downloads))
        {
            try
            {
                var check = await client.CheckAsync();
                Assert.Equal(UpdateCheckStatus.UpdateAvailable, check.Status);
                Assert.Equal("2.0.0", check.Release!.Version);

                var download = await client.DownloadAsync(check.Release);
                Assert.True(download.Success, download.Message);
                Assert.Equal(payload, await File.ReadAllBytesAsync(download.FilePath!));
            }
            finally
            {
                try { Directory.Delete(downloads, recursive: true); } catch { /* 清理失败不影响结论 */ }
            }
        }

        // 客户端升到 2.0.0 后再检测应为已是最新
        using var latestClient = new HttpUpdateService(
            settings,
            new UpdateAddressResolver(Path.Combine(host.DataDirectory, "install")),
            new FixedVersion("2.0.0"),
            NullLogger<HttpUpdateService>.Instance,
            downloadsDirectory: downloads);

        var again = await latestClient.CheckAsync();
        Assert.Equal(UpdateCheckStatus.UpToDate, again.Status);
    }

    private static async Task PublishAsync(UpdateServerHost host, string id)
    {
        using var response = await host.Client.PostAsync($"/api/admin/releases/{id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

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
}
