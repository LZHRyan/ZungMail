using ZungMail.Update.Protocol;
using ZungMail.UpdateServer.Storage;
using Microsoft.Net.Http.Headers;

namespace ZungMail.UpdateServer.Endpoints;

/// <summary>面向客户端的公开接口：探测、检测与安装包下载。</summary>
public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this WebApplication app)
    {
        app.MapGet(UpdateRoutes.Ping, (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new UpdatePingResponse { ServerTime = DateTimeOffset.UtcNow });
        });

        app.MapGet(UpdateRoutes.Check, (HttpContext context, ReleaseStore store) =>
        {
            var currentRaw = context.Request.Query[UpdateRoutes.CurrentParameter].ToString();
            if (!UpdateVersion.TryParse(currentRaw, out var current))
            {
                return Results.Json(
                    new UpdateErrorResponse { Error = "缺少或非法的 current 参数，应形如 1.0.0。" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var channelRaw = context.Request.Query[UpdateRoutes.ChannelParameter].ToString();
            if (!PackageUploader.TryNormalizeChannel(channelRaw, out var channel))
            {
                return Results.Json(
                    new UpdateErrorResponse { Error = "非法的 channel 参数。" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            context.Response.Headers.CacheControl = "no-store";
            var release = store.FindLatestPublished(current, channel);
            var response = new UpdateCheckResponse
            {
                ServerTime = DateTimeOffset.UtcNow,
                Update = release is null ? null : ToReleaseInfo(release)
            };
            return Results.Json(response);
        });

        app.MapGet(UpdateRoutes.DownloadPrefix + "/{id}", (HttpContext context, string id, ReleaseStore store) =>
        {
            var release = store.Find(id);
            if (release is null || !release.Published) return Results.NotFound();

            var path = store.GetPackagePath(release);
            if (!File.Exists(path)) return Results.NotFound();

            // 让客户端/运维可以直接核对哈希；不暴露在客户端展示路径里
            context.Response.Headers["X-Checksum-Sha256"] = release.Sha256;

            // 只有完整下载计入统计，断点续传的后续片段不重复计数
            if (context.Request.Headers.Range.Count == 0)
            {
                _ = store.RecordDownloadAsync(release.Id, CancellationToken.None);
            }

            return Results.File(
                path,
                contentType: "application/octet-stream",
                fileDownloadName: release.FileName,
                lastModified: new DateTimeOffset(release.CreatedAt.UtcDateTime, TimeSpan.Zero),
                entityTag: new EntityTagHeaderValue("\"" + release.Sha256 + "\""),
                enableRangeProcessing: true);
        });

        app.MapGet(UpdateRoutes.Health, (ReleaseStore store, FeedbackStore feedback) =>
        {
            var writable = true;
            string? error = null;
            try
            {
                var probe = Path.Combine(store.DataDirectory, ".health-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                writable = false;
                error = ex.Message;
            }

            // 反馈目录单独探一次：挂卷只读时升级正常、反馈全丢，运维要能一眼看出
            var feedbackWritable = feedback.IsDirectoryWritable();
            var count = store.GetAll().Count(r => r.Published);
            var healthy = writable && feedbackWritable;
            var status = healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
            return Results.Json(
                new
                {
                    status = healthy ? "ok" : "degraded",
                    dataWritable = writable,
                    error,
                    publishedReleases = count,
                    feedbackCount = feedback.Count,
                    feedbackWritable,
                    serverTime = DateTimeOffset.UtcNow
                },
                statusCode: status);
        });
    }

    private static UpdateReleaseInfo ToReleaseInfo(ReleaseRecord release) => new()
    {
        Id = release.Id,
        Version = release.Version,
        Channel = release.Channel,
        Notes = release.Notes,
        Mandatory = release.Mandatory,
        MinVersion = release.MinVersion,
        FileName = release.FileName,
        Size = release.Size,
        Sha256 = release.Sha256,
        PublishedAt = release.PublishedAt ?? release.CreatedAt,
        // 相对路径：客户端按自己配置的根地址拼接，反向代理子路径也能用
        DownloadUrl = UpdateRoutes.Download(release.Id)
    };
}
