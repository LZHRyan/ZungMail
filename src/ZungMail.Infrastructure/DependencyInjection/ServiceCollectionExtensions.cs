using ZungMail.Core.Abstractions;
using ZungMail.Core.Services;
using ZungMail.Infrastructure.Avatars;
using ZungMail.Infrastructure.Configuration;
using ZungMail.Infrastructure.Export;
using ZungMail.Infrastructure.Feedback;
using ZungMail.Infrastructure.Security;
using ZungMail.Infrastructure.Storage;
using ZungMail.Infrastructure.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ZungMail.Infrastructure.DependencyInjection;

/// <summary>基础设施层依赖注入注册。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册路径、连接工厂、数据库初始化、各仓储与支持服务。</summary>
    public static IServiceCollection AddZungMailInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IAppPaths>(_ =>
        {
            var paths = new AppPaths();
            paths.EnsureCreated();
            return paths;
        });
        // 数据目录切换：指针 + 预检 + 启动期迁移
        services.AddSingleton<IDataDirectoryService>(sp =>
            new DataDirectoryService(
                pointer: null,
                logger: sp.GetRequiredService<ILogger<DataDirectoryService>>()));
        // 写信草稿：自动保存与崩溃恢复
        services.AddSingleton<IComposeDraftStore, JsonComposeDraftStore>();
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>(sp =>
        {
            var initializer = new DatabaseInitializer(sp.GetRequiredService<SqliteConnectionFactory>());
            initializer.Initialize();
            return initializer;
        });

        // meta 表的通用键值（一次性回填的「已跑过」标记等）
        services.AddSingleton<IAppMetadataStore>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteMetadataStore(sp.GetRequiredService<SqliteConnectionFactory>());
        });

        // 一次性回填：历史邮件正文里的 base64 内嵌图落盘（缩正文、缩库、加快渲染）
        services.AddSingleton<InlineDataImageBackfillService>();

        // DPAPI 为默认实现；旧 AES-GCM 密文由同一实现的回落通道继续解密，下次保存自动升级。
        services.AddSingleton<ICredentialProtector, DpapiCredentialProtector>();

        services.AddSingleton<IAccountRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>(); // 确保表结构就绪
            return new SqliteAccountRepository(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<ICredentialProtector>());
        });
        services.AddSingleton<IFolderRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteFolderRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<IMessageRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteMessageRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<IContactRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteContactRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<IOutboxRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteOutboxRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<INoteRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteNoteRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<IPendingOperationRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqlitePendingOperationRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<IMailRuleRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteMailRuleRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });

        services.AddSingleton<ISenderFilterRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteSenderFilterRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });

        services.AddSingleton<IMailTagRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteMailTagRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<IReadReceiptRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteReadReceiptRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });        services.AddSingleton<IMessageNoteRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteMessageNoteRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });        services.AddSingleton<IMailContextRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteMailContextRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });        services.AddSingleton<IMailTodoRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteMailTodoRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<IMailReferenceRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteMailReferenceRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<IShipmentRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteShipmentRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<IMailShipmentFieldRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteMailShipmentFieldRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<IMailFollowUpRepository>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new SqliteMailFollowUpRepository(sp.GetRequiredService<SqliteConnectionFactory>());
        });
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<ITemplateStore, JsonTemplateStore>();
        services.AddSingleton<IDataExporter, FileDataExporter>();
        services.AddSingleton<IServerConfigResolver, ServerConfigResolver>();

        // 在线升级：地址解析（用户设置 + 安装目录 update.json）+ HTTP 检测 / 下载
        services.AddSingleton(_ => new UpdateAddressResolver());
        services.AddSingleton<IApplicationVersion, ApplicationVersionProvider>();
        services.AddSingleton<IUpdateService, HttpUpdateService>();

        // 问题反馈：组装环境摘要与脱敏日志 + POST 到升级服务（与升级共用根地址）
        services.AddSingleton(sp => new FeedbackPackageBuilder(
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IApplicationVersion>(),
            sp.GetRequiredService<IMessageRepository>(),
            sp.GetRequiredService<IAccountRepository>(),
            sp.GetRequiredService<IFolderRepository>(),
            sp.GetRequiredService<IAppPaths>().LogDirectory,
            sp.GetRequiredService<ILogger<FeedbackPackageBuilder>>()));
        services.AddSingleton<IFeedbackService, HttpFeedbackService>();

        // 联系人头像：IMAP 没有取头像/通讯录的接口，只能按邮箱哈希查 Gravatar（查不到就保持首字母色块）
        services.AddSingleton<IAvatarService>(sp => new GravatarAvatarService(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<ILogger<GravatarAvatarService>>()));

        // 摘要模型仓库：按需下载到 %LOCALAPPDATA%\ZungMail\models（模型不进安装包）
        services.AddSingleton<ISummaryModelStore>(sp => new SummaryModelStore(
            sp.GetRequiredService<ISettingsStore>(),
            baseDirectory: null,
            sp.GetService<ILogger<SummaryModelStore>>()));

        // 存储空间监控与历史清理（需求 2、3）
        services.AddSingleton<IStorageMonitor, StorageMonitor>();
        services.AddSingleton<ICleanupService>(sp =>
        {
            _ = sp.GetRequiredService<DatabaseInitializer>();
            return new CleanupService(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<IMessageRepository>(),
                sp.GetRequiredService<IAppPaths>(),
                sp.GetRequiredService<ILogger<CleanupService>>());
        });

        return services;
    }
}
