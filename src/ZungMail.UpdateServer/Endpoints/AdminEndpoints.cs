using System.Text;
using ZungMail.Update.Protocol;
using ZungMail.UpdateServer.Security;
using ZungMail.UpdateServer.Storage;

namespace ZungMail.UpdateServer.Endpoints;

/// <summary>后台管理接口：登录、版本列表、上传、编辑、发布 / 下线 / 删除、改口令。</summary>
public static class AdminEndpoints
{
    private const string AdminGroup = "/api/admin";

    public static void MapAdminEndpoints(this WebApplication app)
    {
        // 登录与「当前会话」必须在鉴权过滤器之外，否则未登录时无法知道该不该跳登录页
        app.MapPost(AdminGroup + "/session", LoginAsync);
        app.MapGet(AdminGroup + "/session", GetSession);
        app.MapDelete(AdminGroup + "/session", Logout);

        var admin = app.MapGroup(AdminGroup).AddEndpointFilter<AdminSessionFilter>();

        admin.MapGet("/releases", (HttpContext context, ReleaseStore store) =>
        {
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
            var releases = store.GetAll()
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => ToView(r, store, baseUrl))
                .ToList();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new AdminReleasesView { Releases = releases });
        });

        admin.MapPost("/releases", UploadAsync)
            .DisableAntiforgery();

        admin.MapPut("/releases/{id}", UpdateAsync);

        admin.MapPost("/releases/{id}/publish", async (string id, HttpContext context, ReleaseStore store) =>
        {
            var release = store.Find(id);
            if (release is null) return NotFound();
            if (!File.Exists(store.GetPackagePath(release)))
            {
                return Results.Json(
                    new UpdateErrorResponse { Error = "安装包文件缺失，请删除该记录后重新上传。" },
                    statusCode: StatusCodes.Status409Conflict);
            }

            var updated = await store.SetPublishedAsync(id, true, context.RequestAborted);
            return updated is null ? NotFound() : Results.Json(ToView(updated, store, BaseUrl(context)));
        });

        admin.MapPost("/releases/{id}/unpublish", async (string id, HttpContext context, ReleaseStore store) =>
        {
            var updated = await store.SetPublishedAsync(id, false, context.RequestAborted);
            return updated is null ? NotFound() : Results.Json(ToView(updated, store, BaseUrl(context)));
        });

        admin.MapDelete("/releases/{id}", async (string id, HttpContext context, ReleaseStore store) =>
        {
            var removed = await store.DeleteAsync(id, context.RequestAborted);
            return removed ? Results.NoContent() : NotFound();
        });

        admin.MapGet("/settings", (HttpContext context, ReleaseStore store, AdminAuthService auth, UpdateServerOptions options) =>
        {
            var initial = auth.PasswordSource == AdminPasswordSource.Generated && File.Exists(auth.InitialPasswordPath)
                ? auth.InitialPasswordPath
                : null;

            return Results.Json(new AdminSettingsView
            {
                DataDirectory = store.DataDirectory,
                PasswordSource = auth.PasswordSource.ToString().ToLowerInvariant(),
                CanChangePassword = auth.CanChangePassword,
                InitialPasswordFile = initial,
                CheckUrlHint = BaseUrl(context) + UpdateRoutes.Check + "?current=1.0.0&channel=stable",
                MaxUploadBytes = options.MaxUploadBytes
            });
        });

        admin.MapPost("/password", async (AdminPasswordChangeRequest request, HttpContext context, AdminAuthService auth) =>
        {
            var result = await auth.ChangePasswordAsync(request.CurrentPassword, request.NewPassword, context.RequestAborted);
            if (!result.Success)
            {
                return Results.Json(
                    new UpdateErrorResponse { Error = result.Message },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DeleteCookie(context);
            return Results.Json(new { ok = true, message = result.Message });
        });

        // 用户反馈：列表只读元数据，详情按需读取日志文件
        admin.MapGet("/feedback", (HttpContext context, FeedbackStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new AdminFeedbackListView
            {
                Items = store.GetAll().Select(ToFeedbackView).ToList()
            });
        });

        admin.MapGet("/feedback/{id}", async (string id, HttpContext context, FeedbackStore store) =>
        {
            var record = store.Find(id);
            if (record is null) return FeedbackNotFound();

            context.Response.Headers.CacheControl = "no-store";
            var log = await store.ReadLogAsync(record.Id, context.RequestAborted);
            return Results.Json(new AdminFeedbackDetailView
            {
                Id = record.Id,
                Category = record.Category,
                CategoryLabel = FeedbackCategories.Label(record.Category),
                Description = record.Description,
                Contact = record.Contact,
                ClientVersion = record.ClientVersion,
                Channel = record.Channel,
                Environment = record.Environment,
                Log = log,
                LogTruncated = record.LogTruncated,
                CreatedAt = record.CreatedAt
            });
        });

        admin.MapDelete("/feedback/{id}", async (string id, HttpContext context, FeedbackStore store) =>
        {
            var removed = await store.DeleteAsync(id, context.RequestAborted);
            return removed ? Results.NoContent() : FeedbackNotFound();
        });

        // 日志单独下载：管理页只贴一小段做预览，排查问题时需要拿到完整文件
        admin.MapGet("/feedback/{id}/log", async (string id, HttpContext context, FeedbackStore store) =>
        {
            var record = store.Find(id);
            if (record is null) return FeedbackNotFound();

            var log = await store.ReadLogAsync(record.Id, context.RequestAborted);
            if (string.IsNullOrEmpty(log))
            {
                return Results.Json(
                    new UpdateErrorResponse { Error = "这条反馈没有附加日志。" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            context.Response.Headers.CacheControl = "no-store";
            return Results.File(
                Encoding.UTF8.GetBytes(log),
                contentType: "text/plain; charset=utf-8",
                fileDownloadName: record.Id + ".log");
        });
    }

    private static AdminFeedbackView ToFeedbackView(FeedbackRecord record) => new()
    {
        Id = record.Id,
        Category = record.Category,
        CategoryLabel = FeedbackCategories.Label(record.Category),
        // 列表只给一行摘要，避免整段描述撑开列表
        DescriptionPreview = record.Description.Length <= 120
            ? record.Description
            : record.Description[..120] + "…",
        Contact = record.Contact,
        ClientVersion = record.ClientVersion,
        Channel = record.Channel,
        CreatedAt = record.CreatedAt,
        LogBytes = record.LogBytes,
        HasLog = record.LogBytes > 0,
        LogTruncated = record.LogTruncated
    };

    private static IResult FeedbackNotFound()
        => Results.Json(
            new UpdateErrorResponse { Error = "反馈不存在。" },
            statusCode: StatusCodes.Status404NotFound);

    private static IResult LoginAsync(
        HttpContext context,
        AdminLoginRequest request,
        AdminAuthService auth)
    {
        var key = ClientKey(context);
        if (auth.IsLockedOut(key, out var retryAfter))
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return Results.Json(
                new UpdateErrorResponse { Error = $"口令错误次数过多，请 {Math.Ceiling(retryAfter.TotalMinutes)} 分钟后再试。" },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (!auth.VerifyPassword(request.Password))
        {
            auth.RegisterFailure(key);
            return Results.Json(
                new UpdateErrorResponse { Error = "管理员口令不正确。" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        auth.ResetFailures(key);
        var token = auth.CreateSession();
        var expiry = auth.GetSessionExpiry(token) ?? DateTimeOffset.UtcNow.AddHours(8);
        context.Response.Cookies.Append(AdminAuthService.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = CookiePath(context),
            Expires = expiry
        });
        context.Response.Headers.CacheControl = "no-store";

        return Results.Json(new AdminSessionView
        {
            Authenticated = true,
            ExpiresAt = expiry,
            PasswordSource = auth.PasswordSource.ToString().ToLowerInvariant(),
            CanChangePassword = auth.CanChangePassword
        });
    }

    private static IResult GetSession(HttpContext context, AdminAuthService auth)
    {
        var token = context.Request.Cookies[AdminAuthService.CookieName];
        var authenticated = auth.ValidateSession(token);
        return Results.Json(new AdminSessionView
        {
            Authenticated = authenticated,
            ExpiresAt = authenticated ? auth.GetSessionExpiry(token) : null,
            PasswordSource = auth.PasswordSource.ToString().ToLowerInvariant(),
            CanChangePassword = auth.CanChangePassword
        });
    }

    private static IResult Logout(HttpContext context, AdminAuthService auth)
    {
        auth.RevokeSession(context.Request.Cookies[AdminAuthService.CookieName]);
        DeleteCookie(context);
        return Results.NoContent();
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        PackageUploader uploader,
        ReleaseStore store)
    {
        if (!context.Request.HasFormContentType)
        {
            return Results.Json(
                new UpdateErrorResponse { Error = "请使用 multipart/form-data 上传安装包。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (BadHttpRequestException ex)
        {
            return Results.Json(
                new UpdateErrorResponse { Error = "上传内容过大或被中断：" + ex.Message },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return Results.Json(
                new UpdateErrorResponse { Error = "请选择要上传的完整安装包。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var mandatory = form["mandatory"].ToString();
        var isMandatory = mandatory is "true" or "on" or "1";

        await using var stream = file.OpenReadStream();
        var upload = new PackageUpload(
            stream,
            file.FileName,
            file.Length,
            form["version"].ToString(),
            form["channel"].ToString(),
            form["notes"].ToString(),
            isMandatory,
            form["minVersion"].ToString());

        var result = await uploader.SaveAsync(upload, context.RequestAborted);
        if (!result.Success || result.Record is null)
        {
            return Results.Json(
                new UpdateErrorResponse { Error = result.Error ?? "上传失败。" },
                statusCode: result.StatusCode);
        }

        return Results.Json(ToView(result.Record, store, BaseUrl(context)), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(
        string id,
        AdminReleaseUpdateRequest request,
        HttpContext context,
        ReleaseStore store)
    {
        var existing = store.Find(id);
        if (existing is null) return NotFound();

        if (!UpdateVersion.TryParse(request.Version, out var version))
        {
            return Results.Json(
                new UpdateErrorResponse { Error = "版本号格式不正确，请使用 1.2.3 或 1.2.3.4。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!PackageUploader.TryNormalizeChannel(request.Channel, out var channel))
        {
            return Results.Json(
                new UpdateErrorResponse { Error = "渠道名只能包含小写字母、数字与连字符。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var normalizedVersion = version.ToString();
        if (store.VersionExists(channel, normalizedVersion, exceptId: id))
        {
            return Results.Json(
                new UpdateErrorResponse { Error = $"渠道 {channel} 下已存在版本 {normalizedVersion}。" },
                statusCode: StatusCodes.Status409Conflict);
        }

        string? minVersion = null;
        if (!string.IsNullOrWhiteSpace(request.MinVersion))
        {
            if (!UpdateVersion.TryParse(request.MinVersion, out var min))
            {
                return Results.Json(
                    new UpdateErrorResponse { Error = "最低可升级版本格式不正确。" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (min > version)
            {
                return Results.Json(
                    new UpdateErrorResponse { Error = "最低可升级版本不能高于发布版本。" },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            minVersion = min.ToString();
        }

        var notes = request.Notes ?? "";
        if (notes.Length > 20_000)
        {
            return Results.Json(
                new UpdateErrorResponse { Error = "更新说明过长（最多 20000 字）。" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var updated = await store.UpdateAsync(id, normalizedVersion, channel, notes.Trim(), request.Mandatory, minVersion, context.RequestAborted);
        return updated is null ? NotFound() : Results.Json(ToView(updated, store, BaseUrl(context)));
    }

    private static AdminReleaseView ToView(ReleaseRecord release, ReleaseStore store, string baseUrl) => new()
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
        Published = release.Published,
        CreatedAt = release.CreatedAt,
        PublishedAt = release.PublishedAt,
        DownloadCount = release.DownloadCount,
        FileExists = File.Exists(store.GetPackagePath(release)),
        DownloadUrl = baseUrl + UpdateRoutes.Download(release.Id)
    };

    private static IResult NotFound()
        => Results.Json(
            new UpdateErrorResponse { Error = "版本不存在。" },
            statusCode: StatusCodes.Status404NotFound);

    private static string BaseUrl(HttpContext context)
        => $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}".TrimEnd('/');

    private static string CookiePath(HttpContext context)
        => context.Request.PathBase.HasValue ? context.Request.PathBase.Value! : "/";

    private static void DeleteCookie(HttpContext context)
        => context.Response.Cookies.Delete(AdminAuthService.CookieName, new CookieOptions
        {
            Path = CookiePath(context),
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps
        });

    private static string ClientKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
