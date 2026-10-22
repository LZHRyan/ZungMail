using System.Net;
using System.Text;
using Xunit;

namespace ZungMail.UpdateServer.Tests;

/// <summary>后台管理的认证与会话：401、限流、首启生成口令、改口令后失效。</summary>
public sealed class ServerAuthTests
{
    [Fact]
    public async Task AdminApi_RequiresLogin()
    {
        await using var host = await UpdateServerHost.StartAsync();

        using var response = await host.Client.GetAsync("/api/admin/releases");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("未登录", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_WithCorrectPassword_EstablishesSession()
    {
        await using var host = await UpdateServerHost.StartAsync();

        await host.LoginAsync();

        using var session = await host.Client.GetAsync("/api/admin/session");
        var body = await session.Content.ReadAsStringAsync();
        Assert.Contains("\"authenticated\":true", body);

        using var releases = await host.Client.GetAsync("/api/admin/releases");
        Assert.Equal(HttpStatusCode.OK, releases.StatusCode);
    }

    [Fact]
    public async Task Login_RepeatedFailures_AreRateLimited()
    {
        await using var host = await UpdateServerHost.StartAsync(
            configure: options =>
            {
                options.LoginFailureLimit = 3;
                options.LoginLockout = TimeSpan.FromMinutes(5);
            });

        for (var i = 0; i < 3; i++)
        {
            using var failed = await host.Client.PostAsync(
                "/api/admin/session", Json("wrong-password"));
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        using var locked = await host.Client.PostAsync(
            "/api/admin/session", Json("test-admin-pass-123"));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task GeneratedInitialPassword_CanLoginAndBeChanged()
    {
        var host = await UpdateServerHost.StartAsync(adminPassword: null);
        try
        {
            var initialFile = Path.Combine(host.DataDirectory, "initial-admin-password.txt");
            Assert.True(File.Exists(initialFile), "首启应生成初始口令文件");

            var lines = await File.ReadAllLinesAsync(initialFile);
            var initialPassword = lines[^1].Trim();
            Assert.True(initialPassword.Length >= 16);

            await host.LoginAsync(initialPassword);

            using var change = await host.Client.PostAsync(
                "/api/admin/password",
                Json(new { currentPassword = initialPassword, newPassword = "new-password-123" }));
            Assert.Equal(HttpStatusCode.OK, change.StatusCode);
            Assert.False(File.Exists(initialFile), "改口令后应删除初始口令文件");

            // 改口令会让所有会话失效
            using var afterChange = await host.Client.GetAsync("/api/admin/session");
            Assert.Contains("\"authenticated\":false", await afterChange.Content.ReadAsStringAsync());

            // 旧口令失效、新口令可登录
            using var oldLogin = await host.Client.PostAsync(
                "/api/admin/session", Json(new { password = initialPassword }));
            Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

            await host.LoginAsync("new-password-123");

            // 重启后仍使用新口令（哈希已落盘）
            var restarted = await UpdateServerHost.RestartAsync(host, adminPassword: null);
            try
            {
                await restarted.LoginAsync("new-password-123");
                using var list = await restarted.Client.GetAsync("/api/admin/releases");
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            }
            finally
            {
                await restarted.DisposeAsync();
            }

            host = null!;
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task EnvironmentPassword_CannotBeChangedOnline()
    {
        await using var host = await UpdateServerHost.StartAsync(adminPassword: "env-pass-123");
        await host.LoginAsync("env-pass-123");

        using var change = await host.Client.PostAsync(
            "/api/admin/password",
            Json(new { currentPassword = "env-pass-123", newPassword = "another-pass-123" }));

        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        Assert.Contains("UPDATESERVER_ADMIN_PASSWORD", await change.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Logout_InvalidatesSession()
    {
        await using var host = await UpdateServerHost.StartAsync();
        await host.LoginAsync();

        using var logout = await host.Client.DeleteAsync("/api/admin/session");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var releases = await host.Client.GetAsync("/api/admin/releases");
        Assert.Equal(HttpStatusCode.Unauthorized, releases.StatusCode);
    }

    private static StringContent Json(string password)
        => new($"{{\"password\":\"{password}\"}}", Encoding.UTF8, "application/json");

    private static StringContent Json(object payload)
        => new(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
}
