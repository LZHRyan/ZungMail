using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ZungMail.Update.Protocol;
using Xunit;

namespace ZungMail.UpdateServer.Tests;

/// <summary>安装包上传、草稿 / 发布 / 下线 / 删除、检测与下载的完整 HTTP 行为。</summary>
public sealed class ServerReleaseTests
{
    [Fact]
    public async Task Upload_CreatesDraftThatIsInvisibleToClients()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();
        var payload = CreatePayload(64 * 1024);

        using var response = await host.UploadAsync(payload, "1.1.0", notes: "第一版升级");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("1.1.0", created.GetProperty("version").GetString());
        Assert.False(created.GetProperty("published").GetBoolean());
        Assert.Equal(payload.Length, created.GetProperty("size").GetInt64());
        Assert.Equal(Sha256(payload), created.GetProperty("sha256").GetString());

        var check = await host.CheckAsync("1.0.0");
        Assert.Null(check.Update);

        var releases = await host.GetReleasesAsync();
        Assert.Single(releases.RootElement.GetProperty("releases").EnumerateArray());
    }

    [Fact]
    public async Task Publish_MakesReleaseCheckableAndDownloadable()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();
        var payload = CreatePayload(128 * 1024);
        await host.UploadAsync(payload, "1.1.0", notes: "新增在线升级");
        await PublishAsync(host, "stable-1.1.0");

        var check = await host.CheckAsync("1.0.0");
        Assert.NotNull(check.Update);
        Assert.Equal("1.1.0", check.Update!.Version);
        Assert.Equal("新增在线升级", check.Update.Notes);
        Assert.Equal(payload.Length, check.Update.Size);
        Assert.Equal(Sha256(payload), check.Update.Sha256);
        Assert.Equal("/api/update/download/stable-1.1.0", check.Update.DownloadUrl);

        using var download = await host.Client.GetAsync(check.Update.DownloadUrl);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal(Sha256(payload), download.Headers.GetValues("X-Checksum-Sha256").Single());
        Assert.Equal("ZungMail-setup.exe", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var count = await WaitForDownloadCountAsync(host, expected: 1);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Check_ReturnsNullWhenClientIsNewerOrChannelDiffers()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();
        await host.UploadAsync(CreatePayload(2048), "1.1.0");
        await PublishAsync(host, "stable-1.1.0");

        Assert.Null((await host.CheckAsync("1.2.0")).Update);
        Assert.Null((await host.CheckAsync("1.0.0", channel: "beta")).Update);

        using var missing = await host.Client.GetAsync("/api/update/check?channel=stable");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        using var invalid = await host.Client.GetAsync("/api/update/check?current=abc&channel=stable");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Check_PrefersHighestPublishedVersion()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();
        await host.UploadAsync(CreatePayload(1024), "1.1.0");
        await PublishAsync(host, "stable-1.1.0");
        await host.UploadAsync(CreatePayload(1024), "1.3.0");
        await PublishAsync(host, "stable-1.3.0");
        await host.UploadAsync(CreatePayload(1024), "1.2.0");

        var check = await host.CheckAsync("1.0.0");
        Assert.Equal("1.3.0", check.Update!.Version);
    }

    [Fact]
    public async Task Check_SupportsBetaChannel()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();
        await host.UploadAsync(CreatePayload(1024), "1.2.0-beta.1", channel: "beta");
        await PublishAsync(host, "beta-1.2.0-beta.1");

        var beta = await host.CheckAsync("1.0.0", channel: "beta");
        Assert.Equal("1.2.0-beta.1", beta.Update!.Version);

        var stable = await host.CheckAsync("1.0.0", channel: "stable");
        Assert.Null(stable.Update);
    }

    [Fact]
    public async Task Download_SupportsRangeRequests()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();
        var payload = CreatePayload(4096);
        await host.UploadAsync(payload, "1.1.0");
        await PublishAsync(host, "stable-1.1.0");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/update/download/stable-1.1.0");
        request.Headers.Range = new RangeHeaderValue(1024, 2047);
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(payload[1024..2048], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(4096L, response.Content.Headers.ContentRange!.Length!.Value);
    }

    [Fact]
    public async Task Unpublish_HidesReleaseButKeepsFile()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();
        await host.UploadAsync(CreatePayload(1024), "1.1.0");
        await PublishAsync(host, "stable-1.1.0");

        using var unpublish = await host.Client.PostAsync("/api/admin/releases/stable-1.1.0/unpublish", null);
        Assert.Equal(HttpStatusCode.OK, unpublish.StatusCode);

        Assert.Null((await host.CheckAsync("1.0.0")).Update);

        using var download = await host.Client.GetAsync("/api/update/download/stable-1.1.0");
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);

        var releases = await host.GetReleasesAsync();
        Assert.True(releases.RootElement.GetProperty("releases")[0].GetProperty("fileExists").GetBoolean());
    }

    [Fact]
    public async Task Delete_RemovesRecordAndPackageFile()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();
        await host.UploadAsync(CreatePayload(1024), "1.1.0");
        await PublishAsync(host, "stable-1.1.0");

        var packageDirectory = Path.Combine(host.DataDirectory, "packages", "stable-1.1.0");
        Assert.True(Directory.Exists(packageDirectory));

        using var delete = await host.Client.DeleteAsync("/api/admin/releases/stable-1.1.0");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.False(Directory.Exists(packageDirectory));
        using var download = await host.Client.GetAsync("/api/update/download/stable-1.1.0");
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact]
    public async Task Upload_RejectsInvalidMetadataAndLeavesNoTempFiles()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();

        using var badVersion = await host.UploadAsync(CreatePayload(128), "1.1");
        Assert.Equal(HttpStatusCode.BadRequest, badVersion.StatusCode);

        using var badExtension = await host.UploadAsync(CreatePayload(128), "1.1.0", fileName: "payload.bat");
        Assert.Equal(HttpStatusCode.BadRequest, badExtension.StatusCode);

        using var badMinVersion = await host.UploadAsync(CreatePayload(128), "1.1.0", minVersion: "2.0.0");
        Assert.Equal(HttpStatusCode.BadRequest, badMinVersion.StatusCode);

        var tempDirectory = Path.Combine(host.DataDirectory, "tmp");
        Assert.True(Directory.Exists(tempDirectory));
        Assert.Empty(Directory.GetFiles(tempDirectory));

        var releases = await host.GetReleasesAsync();
        Assert.Empty(releases.RootElement.GetProperty("releases").EnumerateArray());
    }

    [Fact]
    public async Task Upload_RejectsDuplicateVersionWithinChannel()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();
        await host.UploadAsync(CreatePayload(128), "1.1.0");

        using var duplicate = await host.UploadAsync(CreatePayload(128), "1.1.0");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Edit_UpdatesNotesAndMandatoryFlag()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();
        await host.UploadAsync(CreatePayload(1024), "1.1.0", notes: "旧说明");
        await PublishAsync(host, "stable-1.1.0");

        using var update = await host.Client.PutAsync(
            "/api/admin/releases/stable-1.1.0",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    version = "1.1.0",
                    channel = "stable",
                    notes = "新说明：必须升级",
                    mandatory = true,
                    minVersion = "1.0.0"
                }),
                System.Text.Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var check = await host.CheckAsync("1.0.0");
        Assert.Equal("新说明：必须升级", check.Update!.Notes);
        Assert.True(check.Update.Mandatory);
        Assert.Equal("1.0.0", check.Update.MinVersion);
    }

    [Fact]
    public async Task Release_SurvivesRestart()
    {
        var host = await UpdateServerHost.StartAsync();
        var data = host.DataDirectory;
        try
        {
            await host.LoginAsync();
            await host.UploadAsync(CreatePayload(2048), "1.1.0");
            await PublishAsync(host, "stable-1.1.0");
        }
        finally
        {
            await host.StopAsync(deleteData: false);
        }

        var restarted = await UpdateServerHost.StartAsync(dataDirectory: data);
        try
        {
            await restarted.LoginAsync();
            var releases = await restarted.GetReleasesAsync();
            Assert.Single(releases.RootElement.GetProperty("releases").EnumerateArray());

            var check = await restarted.CheckAsync("1.0.0");
            Assert.Equal("1.1.0", check.Update!.Version);

            using var download = await restarted.Client.GetAsync(check.Update.DownloadUrl);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        }
        finally
        {
            await restarted.DisposeAsync();
        }
    }

    private static async Task PublishAsync(UpdateServerHost host, string id)
    {
        using var response = await host.Client.PostAsync($"/api/admin/releases/{id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<int> WaitForDownloadCountAsync(UpdateServerHost host, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var releases = await host.GetReleasesAsync();
            var count = releases.RootElement.GetProperty("releases")[0].GetProperty("downloadCount").GetInt32();
            if (count >= expected) return count;
            await Task.Delay(50);
        }

        return -1;
    }

    private static byte[] CreatePayload(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        return bytes;
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
