using System.Text;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Feedback;
using ZungMail.Infrastructure.Update;
using ZungMail.Update.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 反馈内容组装与提交：环境摘要不含隐私、日志脱敏与截断、请求体积收敛，
/// 以及成功 / 未配置 / 不可达 / 400 / 429 的结果分类。
/// </summary>
public sealed class FeedbackTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mc-feedback-tests", Guid.NewGuid().ToString("N"));

    private readonly string _logs;

    public FeedbackTests()
    {
        _logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(_logs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试目录清理失败不影响结论 */ }
    }

    // ---------------- 组装 ----------------

    [Fact]
    public async Task Build_IncludesVersionAndEnvironmentWithoutPrivateData()
    {
        var builder = CreateBuilder();

        var request = await builder.BuildRequestAsync(new FeedbackDraft(
            FeedbackCategories.Malfunction, "打开附件时客户端闪退，重复三次都这样", "ops@forwarder.cn", false, true));

        Assert.Equal(FeedbackCategories.Malfunction, request.Category);
        Assert.Equal("1.0.0", request.ClientVersion);
        Assert.NotNull(request.Environment);
        Assert.Contains("客户端版本：1.0.0", request.Environment);
        Assert.Contains("操作系统：", request.Environment);
        Assert.Contains(".NET 运行时：", request.Environment);
        // 环境摘要只放版本与系统，绝不带联系方式或地址
        Assert.DoesNotContain("@", request.Environment);
        Assert.DoesNotContain("ops@forwarder.cn", request.Environment);
        Assert.DoesNotContain("password", request.Environment, StringComparison.OrdinalIgnoreCase);
        Assert.Null(request.Log);
    }

    [Fact]
    public async Task Build_LeavesEnvironmentAndLogEmptyWhenUnchecked()
    {
        File.WriteAllText(Path.Combine(_logs, "app-20261019.log"), "info: 启动完成");

        var request = await CreateBuilder().BuildRequestAsync(new FeedbackDraft(
            FeedbackCategories.Other, "只是想问一下签名怎么设置才生效", null, false, false));

        Assert.Null(request.Environment);
        Assert.Null(request.Log);
        Assert.Null(request.Contact);
    }

    [Fact]
    public async Task Build_SanitizesEmailsAndSecrets()
    {
        File.WriteAllText(
            Path.Combine(_logs, "app-20261019.log"),
            """
            2026-10-19 10:00:01 [INFO] 登录账户 ops@forwarder.cn 成功
            2026-10-19 10:00:02 [DEBUG] connect password=abc123 secret=s3cr3t
            2026-10-19 10:00:03 [DEBUG] Authorization: Bearer eyJhbGciOiJIUzI1NiJ9
            2026-10-19 10:00:04 [INFO] 同步完成：新增 3 封，buyer@customer.com 未读
            """);

        var request = await CreateBuilder().BuildRequestAsync(new FeedbackDraft(
            FeedbackCategories.Question, "同步后有几个账号收不到信，想确认是不是配置问题", null, true, false));

        Assert.NotNull(request.Log);
        Assert.Contains("<email>", request.Log);
        Assert.DoesNotContain("forwarder.cn", request.Log);
        Assert.DoesNotContain("customer.com", request.Log);
        Assert.Contains("password=<redacted>", request.Log);
        Assert.DoesNotContain("abc123", request.Log);
        Assert.DoesNotContain("s3cr3t", request.Log);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", request.Log);
    }

    [Fact]
    public void Sanitize_HandlesEmptyAndPlainText()
    {
        Assert.Equal("", FeedbackPackageBuilder.SanitizeLog(null));
        Assert.Equal("没有敏感信息", FeedbackPackageBuilder.SanitizeLog("没有敏感信息"));
    }

    [Fact]
    public async Task Build_TruncatesHugeLogToBudget()
    {
        var line = new string('x', 200) + Environment.NewLine;
        var content = new StringBuilder();
        while (content.Length < 2 * 1024 * 1024) content.Append(line);
        File.WriteAllText(Path.Combine(_logs, "app-20261019.log"), content.ToString());

        var request = await CreateBuilder().BuildRequestAsync(new FeedbackDraft(
            FeedbackCategories.Malfunction, "长时间同步后客户端卡住不动，任务管理器里内存一直涨", null, true, true));

        Assert.NotNull(request.Log);
        Assert.True(request.LogTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(request.Log!) <= UpdateRoutes.MaxFeedbackLogBytes + 200);
        Assert.True(request.SerializedSize() <= UpdateRoutes.MaxFeedbackBytes);
    }

    [Fact]
    public async Task Build_ReportsMissingLogDirectory()
    {
        Directory.Delete(_logs, recursive: true);
        var builder = CreateBuilder();

        var info = builder.InspectLog();
        var request = await builder.BuildRequestAsync(new FeedbackDraft(
            FeedbackCategories.Other, "安装后第一次启动就提示数据目录不可写，换目录也一样", null, true, false));

        Assert.False(info.Available);
        Assert.Null(request.Log);
    }

    [Fact]
    public void InspectLog_DescribesAttachableFile()
    {
        File.WriteAllText(Path.Combine(_logs, "app-20261019.log"), new string('y', 4096));

        var info = CreateBuilder().InspectLog();

        Assert.True(info.Available);
        Assert.Contains("app-20261019.log", info.Description);
        Assert.True(info.Bytes > 0);
    }

    // ---------------- 提交 ----------------

    [Fact]
    public async Task Submit_ReturnsFeedbackIdOnSuccess()
    {
        await using var server = await TestHttpServer.StartAsync(request =>
            request.Path == UpdateRoutes.Feedback
                ? TestHttpServer.Json(new FeedbackResponse
                {
                    Id = "fb-20261019-001",
                    ReceivedAt = DateTimeOffset.Now
                })
                : TestHttpServer.NotFound());

        using var service = CreateService(server.BaseUrl);
        var result = await service.SubmitAsync(await BuildSimpleRequestAsync(service));

        Assert.True(result.Success);
        Assert.Equal("fb-20261019-001", result.FeedbackId);
        Assert.Contains("fb-20261019-001", result.Message);
        Assert.Contains("POST " + UpdateRoutes.Feedback, server.Requests[0]);
    }

    [Fact]
    public async Task Submit_ClassifiesRateLimit()
    {
        await using var server = await TestHttpServer.StartAsync(_ =>
            TestHttpServer.Json(new UpdateErrorResponse { Error = "提交过于频繁，请稍后再试。" }, statusCode: 429));

        using var service = CreateService(server.BaseUrl);
        var result = await service.SubmitAsync(await BuildSimpleRequestAsync(service));

        Assert.Equal(FeedbackSubmitStatus.RateLimited, result.Status);
        Assert.Contains("过于频繁", result.Message);
        Assert.Null(result.FeedbackId);
    }

    [Fact]
    public async Task Submit_ClassifiesRejectedRequest()
    {
        await using var server = await TestHttpServer.StartAsync(_ =>
            TestHttpServer.Json(new UpdateErrorResponse { Error = "描述不能为空。" }, statusCode: 400));

        using var service = CreateService(server.BaseUrl);
        var result = await service.SubmitAsync(await BuildSimpleRequestAsync(service));

        Assert.Equal(FeedbackSubmitStatus.Rejected, result.Status);
        Assert.Contains("描述不能为空", result.Message);
    }

    [Fact]
    public async Task Submit_ClassifiesUnreachable()
    {
        var probe = new TestHttpServer();
        var port = probe.Port;
        await probe.DisposeAsync();

        using var service = CreateService($"http://127.0.0.1:{port}");
        var result = await service.SubmitAsync(await BuildSimpleRequestAsync(service));

        Assert.Equal(FeedbackSubmitStatus.Unreachable, result.Status);
        Assert.Contains("无法连接升级服务", result.Message);
    }

    [Fact]
    public async Task Submit_DoesNotRequestWhenNotConfigured()
    {
        await using var server = await TestHttpServer.StartAsync(_ =>
            TestHttpServer.Json(new FeedbackResponse { Id = "fb-20261019-002" }));

        var install = Path.Combine(_root, "no-config");
        Directory.CreateDirectory(install);
        var settings = new FeedbackSettingsStore(new AppSettings());
        var builder = new FeedbackPackageBuilder(
            settings,
            new FixedVersion("1.0.0"),
            logDirectory: _logs);
        using var service = new HttpFeedbackService(
            builder,
            settings,
            new UpdateAddressResolver(install),
            new FixedVersion("1.0.0"),
            NullLogger<HttpFeedbackService>.Instance);

        var result = await service.SubmitAsync(await BuildSimpleRequestAsync(service));

        Assert.Equal(FeedbackSubmitStatus.NotConfigured, result.Status);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Submit_ClassifiesInvalidAddress()
    {
        using var service = CreateService("ftp://updates.example.com");
        var result = await service.SubmitAsync(await BuildSimpleRequestAsync(service));

        Assert.Equal(FeedbackSubmitStatus.InvalidAddress, result.Status);
        Assert.Contains("http", result.Message);
    }

    // ---------------- 工具 ----------------

    private FeedbackPackageBuilder CreateBuilder(AppSettings? settings = null)
        => new(
            new FeedbackSettingsStore(settings ?? new AppSettings()),
            new FixedVersion("1.0.0"),
            logDirectory: _logs);

    private HttpFeedbackService CreateService(string baseUrl)
    {
        var settings = new FeedbackSettingsStore(new AppSettings { UpdateServiceUrl = baseUrl });
        var install = Path.Combine(_root, "install-empty");
        Directory.CreateDirectory(install);
        return new HttpFeedbackService(
            new FeedbackPackageBuilder(settings, new FixedVersion("1.0.0"), logDirectory: _logs),
            settings,
            new UpdateAddressResolver(install),
            new FixedVersion("1.0.0"),
            NullLogger<HttpFeedbackService>.Instance);
    }

    private static Task<FeedbackRequest> BuildSimpleRequestAsync(IFeedbackService service)
        => service.BuildRequestAsync(new FeedbackDraft(
            FeedbackCategories.Suggestion,
            "希望邮件列表能按业务单号快速分组，现在只能靠搜索",
            null,
            false,
            false));

    private sealed class FeedbackSettingsStore(AppSettings settings) : ISettingsStore
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
