using ZungMail.UpdateServer.Endpoints;
using ZungMail.UpdateServer.Security;
using ZungMail.UpdateServer.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace ZungMail.UpdateServer;

/// <summary>
/// 组装升级服务。抽成静态方法是为了让集成测试能用临时数据目录启动真实 Kestrel，
/// 而不是再复制一份 DI / 路由配置。
/// </summary>
public static class ServerBootstrap
{
    public static WebApplication Build(
        string[] args,
        Action<UpdateServerOptions>? configure = null,
        string? contentRoot = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = ResolveContentRoot(contentRoot)
        });

        var options = UpdateServerOptions.FromConfiguration(builder.Configuration);
        configure?.Invoke(options);
        if (!Path.IsPathRooted(options.DataDirectory))
        {
            options.DataDirectory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, options.DataDirectory));
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ReleaseStore>();
        builder.Services.AddSingleton<PackageUploader>();
        builder.Services.AddSingleton<AdminAuthService>();
        builder.Services.AddSingleton<FeedbackStore>();
        builder.Services.AddSingleton<FeedbackRateLimiter>();

        // 完整安装包可能有上百 MB：同时放宽 Kestrel 与表单限制
        builder.Services.Configure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = options.MaxUploadBytes);
        builder.Services.Configure<FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = options.MaxUploadBytes;
            o.ValueLengthLimit = 256 * 1024;
        });

        var app = builder.Build();

        // 反向代理（Nginx / Traefik）后要能拿到真实协议与来源 IP，管理会话 Cookie 的 Secure 判断才正确。
        // 容器里代理是另一个 IP，默认只信任 loopback 会拿不到头，因此显式信任所有代理；
        // 服务应只监听内网或由代理转发，不要直接暴露到公网。
        var forwardedHeaders = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        forwardedHeaders.KnownNetworks.Clear();
        forwardedHeaders.KnownProxies.Clear();
        app.UseForwardedHeaders(forwardedHeaders);

        var pathBase = app.Configuration["UPDATESERVER_PATH_BASE"];
        if (!string.IsNullOrWhiteSpace(pathBase) && pathBase.StartsWith('/'))
        {
            app.UsePathBase(pathBase.TrimEnd('/'));
        }

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapUpdateEndpoints();
        app.MapFeedbackEndpoints();
        app.MapAdminEndpoints();

        // 初始化存储与管理员口令：失败就让服务起不来，避免带着半初始化状态对外提供下载
        var store = app.Services.GetRequiredService<ReleaseStore>();
        store.InitializeAsync().GetAwaiter().GetResult();
        store.CleanTempDirectory();

        var feedback = app.Services.GetRequiredService<FeedbackStore>();
        feedback.InitializeAsync().GetAwaiter().GetResult();

        var auth = app.Services.GetRequiredService<AdminAuthService>();
        auth.InitializeAsync().GetAwaiter().GetResult();

        app.Logger.LogInformation(
            "ZungMail 升级服务已就绪：数据目录 {DataDirectory}，管理员口令来源 {Source}。",
            store.DataDirectory,
            auth.PasswordSource);

        return app;
    }

    /// <summary>
    /// 定位内容根（决定 `wwwroot` 与 appsettings 的位置）：
    /// 发布后就是程序目录；`dotnet run` 时程序目录在 bin/Debug 下没有 wwwroot，
    /// 需要向上找到工程目录，否则管理页会 404。
    /// </summary>
    private static string ResolveContentRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return explicitRoot;

        var baseDirectory = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(baseDirectory, "wwwroot"))) return baseDirectory;

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ZungMail.UpdateServer.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return baseDirectory;
    }
}
