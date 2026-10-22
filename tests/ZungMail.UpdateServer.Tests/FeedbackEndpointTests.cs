using System.Net;
using System.Text;
using System.Text.Json;
using ZungMail.Update.Protocol;
using ZungMail.UpdateServer;
using Xunit;

namespace ZungMail.UpdateServer.Tests;

/// <summary>
/// 用户反馈接口：公开提交、描述校验、体积与频率限制、管理页查看 / 删除、
/// 条数上限淘汰与重启后持久化。
/// </summary>
public sealed class FeedbackEndpointTests
{
    [Fact]
    public async Task Submit_SavesRecordAndLog()
    {
        await using var host = await UpdateServerHost.StartAsync();

        using var response = await host.Client.PostAsync(UpdateRoutes.Feedback, Json(BuildRequest("附件双击打不开，提示格式不支持。")));
        var payload = await ReadAsync<FeedbackResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("fb-", payload.Id);
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "feedback", payload.Id + ".json")));
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "feedback", payload.Id + ".log")));
    }

    [Fact]
    public async Task Submit_RejectsEmptyDescription()
    {
        await using var host = await UpdateServerHost.StartAsync();

        using var response = await host.Client.PostAsync(UpdateRoutes.Feedback, Json(BuildRequest("   ")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(host.DataDirectory, "feedback"))
                     && Directory.GetFiles(Path.Combine(host.DataDirectory, "feedback"), "*.json").Length > 0);
    }

    [Fact]
    public async Task Submit_RejectsOversizedBody()
    {
        await using var host = await UpdateServerHost.StartAsync(
            configure: options => options.MaxFeedbackBytes = 2048);

        var request = BuildRequest(new string('长', 8000));
        using var response = await host.Client.PostAsync(UpdateRoutes.Feedback, Json(request));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Submit_RateLimitsRepeatedSubmissions()
    {
        await using var host = await UpdateServerHost.StartAsync(
            configure: options => options.FeedbackPerHourLimit = 2);

        for (var i = 0; i < 2; i++)
        {
            using var ok = await host.Client.PostAsync(UpdateRoutes.Feedback, Json(BuildRequest("第一次提交的反馈内容，用于验证限流。")));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await host.Client.PostAsync(UpdateRoutes.Feedback, Json(BuildRequest("第三次提交应当被限流拦住。")));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null);
    }

    [Fact]
    public async Task AdminFeedback_RequiresAdminSession()
    {
        await using var host = await UpdateServerHost.StartAsync();
        using var response = await host.Client.GetAsync(UpdateRoutes.AdminFeedback);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminFeedback_ListsDetailsAndDeletes()
    {
        await using var host = await UpdateServerHost.StartAsync();
        using var submitted = await host.Client.PostAsync(UpdateRoutes.Feedback, Json(BuildRequest("发送带大附件时进度条卡在 99%。")));
        var created = await ReadAsync<FeedbackResponse>(submitted);

        await host.LoginAsync();

        var list = await host.Client.GetAsync(UpdateRoutes.AdminFeedback);
        var listPayload = await ReadAsync<JsonElement>(list);
        var items = listPayload.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(created.Id, items[0].GetProperty("id").GetString());
        Assert.Equal("功能异常", items[0].GetProperty("categoryLabel").GetString());

        var detail = await host.Client.GetAsync(UpdateRoutes.AdminFeedbackItem(created.Id));
        var detailPayload = await ReadAsync<JsonElement>(detail);
        Assert.Contains("ops@forwarder.cn", detailPayload.GetProperty("log").GetString());
        Assert.Equal("客户端版本：1.0.0", detailPayload.GetProperty("environment").GetString());

        using var deleted = await host.Client.DeleteAsync(UpdateRoutes.AdminFeedbackItem(created.Id));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "feedback", created.Id + ".json")));
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "feedback", created.Id + ".log")));

        var after = await ReadAsync<JsonElement>(await host.Client.GetAsync(UpdateRoutes.AdminFeedback));
        Assert.Equal(0, after.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Store_DropsOldestRecordsBeyondLimit()
    {
        await using var host = await UpdateServerHost.StartAsync(
            configure: options => options.MaxFeedbackRecords = 2);

        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await host.Client.PostAsync(
                UpdateRoutes.Feedback,
                Json(BuildRequest($"第 {i + 1} 条反馈：希望支持按单号批量操作。")));
            ids.Add((await ReadAsync<FeedbackResponse>(response)).Id);
            await Task.Delay(20);
        }

        await host.LoginAsync();
        var list = await ReadAsync<JsonElement>(await host.Client.GetAsync(UpdateRoutes.AdminFeedback));

        Assert.Equal(2, list.GetProperty("items").GetArrayLength());
        Assert.False(File.Exists(Path.Combine(host.DataDirectory, "feedback", ids[0] + ".json")));
        Assert.True(File.Exists(Path.Combine(host.DataDirectory, "feedback", ids[2] + ".json")));
    }

    [Fact]
    public async Task Health_ReportsFeedbackCapacity()
    {
        await using var host = await UpdateServerHost.StartAsync();
        using var submitted = await host.Client.PostAsync(UpdateRoutes.Feedback, Json(BuildRequest("健康检查里要能看到反馈数量。")));

        using var response = await host.Client.GetAsync(UpdateRoutes.Health);
        var payload = await ReadAsync<JsonElement>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, payload.GetProperty("feedbackCount").GetInt32());
        Assert.True(payload.GetProperty("feedbackWritable").GetBoolean());
    }

    [Fact]
    public async Task Feedback_SurvivesRestart()
    {
        var data = Path.Combine(Path.GetTempPath(), "mc-feedback-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);

        var first = await UpdateServerHost.StartAsync(dataDirectory: data);
        string id;
        try
        {
            using var response = await first.Client.PostAsync(UpdateRoutes.Feedback, Json(BuildRequest("重启后这条反馈还要能看到。")));
            id = (await ReadAsync<FeedbackResponse>(response)).Id;
        }
        finally
        {
            await first.StopAsync(deleteData: false);
        }

        await using var second = await UpdateServerHost.StartAsync(dataDirectory: data);
        await second.LoginAsync();
        var list = await ReadAsync<JsonElement>(await second.Client.GetAsync(UpdateRoutes.AdminFeedback));

        Assert.Equal(1, list.GetProperty("items").GetArrayLength());
        Assert.Equal(id, list.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task LogDownload_ReturnsAttachment()
    {
        await using var host = await UpdateServerHost.StartAsync();
        using var submitted = await host.Client.PostAsync(UpdateRoutes.Feedback, Json(BuildRequest("日志要能下载下来自己搜。")));
        var created = await ReadAsync<FeedbackResponse>(submitted);

        await host.LoginAsync();

        using var response = await host.Client.GetAsync(UpdateRoutes.AdminFeedbackLog(created.Id));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"{created.Id}.log", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Contains("ops@forwarder.cn", text);
    }

    [Fact]
    public async Task LogDownload_RequiresAdminSession()
    {
        await using var host = await UpdateServerHost.StartAsync();
        using var submitted = await host.Client.PostAsync(UpdateRoutes.Feedback, Json(BuildRequest("未登录不能下载日志。")));
        var created = await ReadAsync<FeedbackResponse>(submitted);

        using var response = await host.Client.GetAsync(UpdateRoutes.AdminFeedbackLog(created.Id));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LogDownload_ReturnsNotFoundWhenFeedbackHasNoLog()
    {
        await using var host = await UpdateServerHost.StartAsync();
        var request = BuildRequest("这条反馈故意不带日志。");
        request.Log = null;

        using var submitted = await host.Client.PostAsync(UpdateRoutes.Feedback, Json(request));
        var created = await ReadAsync<FeedbackResponse>(submitted);

        await host.LoginAsync();
        using var response = await host.Client.GetAsync(UpdateRoutes.AdminFeedbackLog(created.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------- 工具 ----------------

    private static FeedbackRequest BuildRequest(string description) => new()
    {
        Category = FeedbackCategories.Malfunction,
        Description = description,
        Contact = "ops@forwarder.cn",
        ClientVersion = "1.0.0",
        Channel = "stable",
        Environment = "客户端版本：1.0.0",
        Log = "2026-10-19 10:00:01 [INFO] 登录账户 ops@forwarder.cn 成功",
        SubmittedAt = DateTimeOffset.Now
    };

    private static StringContent Json(object payload)
        => new(JsonSerializer.Serialize(payload, UpdateJson.Options), Encoding.UTF8, "application/json");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(text, UpdateJson.Options)!;
    }
}
