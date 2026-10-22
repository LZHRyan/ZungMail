using System.Text.Json;
using ZungMail.Update.Protocol;
using ZungMail.UpdateServer.Security;
using ZungMail.UpdateServer.Storage;

namespace ZungMail.UpdateServer.Endpoints;

/// <summary>
/// 公开的反馈接收接口：客户端在「设置 → 反馈」里提交，不需要管理员会话。
/// 校验描述、限制体积与频率，其余内容原样落盘交给管理页处理。
/// </summary>
public static class FeedbackEndpoints
{
    private const int MaxDescriptionLength = 4000;

    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        app.MapPost(UpdateRoutes.Feedback, SubmitAsync).DisableAntiforgery();
    }

    private static async Task<IResult> SubmitAsync(
        HttpContext context,
        FeedbackStore store,
        FeedbackRateLimiter limiter,
        UpdateServerOptions options,
        CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";

        // 声明长度就先拦一刀，避免把上百 MB 的请求读进内存
        if (context.Request.ContentLength is { } declared && declared > options.MaxFeedbackBytes)
        {
            return TooLarge(options);
        }

        var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!limiter.TryAcquire(source, out var retryAfter))
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return Results.Json(
                new UpdateErrorResponse { Error = "提交过于频繁，请稍后再试。" },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        FeedbackRequest? request;
        try
        {
            request = await ReadJsonAsync(context.Request.Body, options.MaxFeedbackBytes, ct).ConfigureAwait(false);
        }
        catch (BodyTooLargeException)
        {
            return TooLarge(options);
        }
        catch (JsonException)
        {
            return Results.Json(
                new UpdateErrorResponse { Error = "请求体不是有效的反馈 JSON。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request is null)
        {
            return Results.Json(
                new UpdateErrorResponse { Error = "请求体不能为空。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var description = (request.Description ?? "").Trim();
        if (description.Length == 0)
        {
            return Results.Json(
                new UpdateErrorResponse { Error = "描述不能为空。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (description.Length > MaxDescriptionLength)
        {
            return Results.Json(
                new UpdateErrorResponse { Error = $"描述过长（最多 {MaxDescriptionLength} 字）。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var record = await store.AddAsync(request, request.Log, ct).ConfigureAwait(false);
        return Results.Json(new FeedbackResponse
        {
            Id = record.Id,
            ReceivedAt = record.CreatedAt
        });
    }

    private static IResult TooLarge(UpdateServerOptions options) => Results.Json(
        new UpdateErrorResponse
        {
            Error = $"反馈内容过大（上限 {options.MaxFeedbackBytes / 1024} KB），请减少附加内容后重试。"
        },
        statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>边读边计数：超过上限立即停止读取，不依赖 Content-Length 是否可信。</summary>
    private static async Task<FeedbackRequest?> ReadJsonAsync(
        Stream body,
        int maxBytes,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await body.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read <= 0) break;

            total += read;
            if (total > maxBytes) throw new BodyTooLargeException();
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0) return null;
        buffer.Position = 0;
        return await JsonSerializer
            .DeserializeAsync<FeedbackRequest>(buffer, UpdateJson.Options, ct)
            .ConfigureAwait(false);
    }

    private sealed class BodyTooLargeException : Exception;
}
