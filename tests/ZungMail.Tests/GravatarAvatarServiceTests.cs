using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ZungMail.Core.Abstractions;
using ZungMail.Infrastructure.Avatars;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// Gravatar 头像来源：邮箱哈希算法、缓存与「服务器没有」标记的语义。
/// 网络一律用桩 handler，测试不依赖外网。
/// </summary>
public sealed class GravatarAvatarServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mc-avatar-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 测试产物清理失败不影响结论
        }
    }

    /// <summary>Gravatar 官方文档给的示例：邮箱 trim 后转小写再取 MD5。</summary>
    [Theory]
    [InlineData("MyEmailAddress@example.com ", "0bc83cb571cd1c50ba6f3e8a78ef1346")]
    [InlineData("  MYEMAILADDRESS@EXAMPLE.COM", "0bc83cb571cd1c50ba6f3e8a78ef1346")]
    [InlineData("ops@forwarder.cn", null)]
    public void HashEmail_NormalizesLikeGravatar(string email, string? expected)
    {
        var hash = GravatarAvatarService.HashEmail(email);

        Assert.Equal(32, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        if (expected is not null) Assert.Equal(expected, hash);
    }

    [Theory]
    [InlineData("a@b.cn", true)]
    [InlineData("  Ops@Forwarder.CN \t", true)]
    [InlineData("", false)]
    [InlineData("no-at-sign", false)]
    [InlineData("bad @example.com", false)]
    public void TryNormalizeEmail_RejectsNonAddresses(string input, bool expected)
        => Assert.Equal(expected, GravatarAvatarService.TryNormalizeEmail(input, out _));

    [Fact]
    public async Task GetAsync_Hit_IsCachedOnDiskAndReused()
    {
        var handler = new StubHandler(HttpStatusCode.OK, [0x89, 0x50, 0x4E, 0x47]);
        using var service = CreateService(handler);

        var first = await service.GetAsync("ops@forwarder.cn");

        Assert.Equal(AvatarLookupStatus.Hit, first.Status);
        Assert.True(first.HasImage);
        Assert.Equal(1, handler.Requests);
        Assert.True(File.Exists(Path.Combine(service.CacheDirectory,
            GravatarAvatarService.HashEmail("ops@forwarder.cn") + ".bin")));

        // 第二次同地址：直接读缓存，不再发请求
        var second = await service.GetAsync("OPS@forwarder.cn");
        Assert.Equal(AvatarLookupStatus.Cached, second.Status);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task GetAsync_NotFound_WritesMissMarkerAndSkipsRepeatRequest()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, []);
        using var service = CreateService(handler);

        var first = await service.GetAsync("nobody@example.com");

        Assert.Equal(AvatarLookupStatus.NotFound, first.Status);
        Assert.False(first.HasImage);
        Assert.Equal(1, handler.Requests);

        // 7 天内的 miss 标记让第二次调用直接返回，不再打服务器
        var second = await service.GetAsync("nobody@example.com");
        Assert.Equal(AvatarLookupStatus.NotFound, second.Status);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task GetAsync_ServerError_DoesNotWriteMissMarker()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, []);
        using var service = CreateService(handler);

        var first = await service.GetAsync("ops@forwarder.cn");
        Assert.Equal(AvatarLookupStatus.Unavailable, first.Status);

        // 没写 miss：下次运行还会再试（这里用第二次调用验证确实重发了请求）
        await service.GetAsync("ops@forwarder.cn");
        Assert.Equal(2, handler.Requests);
        Assert.False(File.Exists(Path.Combine(service.CacheDirectory,
            GravatarAvatarService.HashEmail("ops@forwarder.cn") + ".miss")));
    }

    [Fact]
    public async Task GetAsync_InvalidEmail_ReturnsNotFoundWithoutRequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, [1, 2, 3]);
        using var service = CreateService(handler);

        var result = await service.GetAsync("not-an-email");

        Assert.Equal(AvatarLookupStatus.NotFound, result.Status);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task GetAsync_NullOrEmptyEmail_IsSafe()
    {
        var handler = new StubHandler(HttpStatusCode.OK, [1]);
        using var service = CreateService(handler);

        Assert.Equal(AvatarLookupStatus.NotFound, (await service.GetAsync(null)).Status);
        Assert.Equal(AvatarLookupStatus.NotFound, (await service.GetAsync("   ")).Status);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task GetAsync_Cancelled_ReturnsUnavailableWithoutThrowing()
    {
        var handler = new StubHandler(HttpStatusCode.OK, [1, 2, 3]) { Delay = TimeSpan.FromSeconds(10) };
        using var service = CreateService(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.GetAsync("ops@forwarder.cn", cts.Token);

        Assert.Equal(AvatarLookupStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task GetAsync_OversizedPayload_IsRejected()
    {
        var handler = new StubHandler(HttpStatusCode.OK, new byte[600 * 1024]);
        using var service = CreateService(handler);

        var result = await service.GetAsync("ops@forwarder.cn");

        Assert.Equal(AvatarLookupStatus.Unavailable, result.Status);
        Assert.Equal(1, handler.Requests);
    }

    private GravatarAvatarService CreateService(StubHandler handler)
    {
        Directory.CreateDirectory(_root);
        return new GravatarAvatarService(new TempPaths(_root), NullLogger<GravatarAvatarService>.Instance, handler);
    }

    /// <summary>可编排状态码 / 体积 / 延迟的桩 handler，并记录请求次数。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _body;

        public StubHandler(HttpStatusCode status, byte[] body)
        {
            _status = status;
            _body = body;
        }

        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            var response = new HttpResponseMessage(_status);
            if (_body.Length > 0) response.Content = new ByteArrayContent(_body);
            return response;
        }
    }

    /// <summary>只提供根目录的最小 IAppPaths 实现（头像目录由服务自行派生）。</summary>
    private sealed class TempPaths : IAppPaths
    {
        public TempPaths(string root) => RootDirectory = root;

        public string RootDirectory { get; }

        public string DatabasePath => Path.Combine(RootDirectory, "mail.db");

        public string TemplatesPath => Path.Combine(RootDirectory, "templates.json");

        public string SettingsPath => Path.Combine(RootDirectory, "settings.json");

        public string LogDirectory => Path.Combine(RootDirectory, "logs");

        public string EmlDirectory => Path.Combine(RootDirectory, "eml");

        public void EnsureCreated() => Directory.CreateDirectory(RootDirectory);
    }
}
