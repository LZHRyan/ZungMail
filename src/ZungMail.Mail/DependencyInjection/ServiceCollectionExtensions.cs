using ZungMail.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ZungMail.Mail.DependencyInjection;

/// <summary>协议层（MailKit）依赖注入注册。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 IMAP 同步引擎、SMTP 发送器与待发送队列处理器。</summary>
    public static IServiceCollection AddZungMailMail(this IServiceCollection services)
    {
        // 旧式字符集（GB2312 / KOI8-R / Windows-1251 等）必须在任何 MailKit 解析之前注册
        MailCharsets.EnsureRegistered();

        // 同步引擎同时作为「已发送归档器」被发件箱复用，故注册单例后按接口暴露。
        services.AddSingleton<IImapClientFactory, MailKitImapClientFactory>();
        services.AddSingleton<ImapSyncService>();
        services.AddSingleton<IMailSyncService>(sp => sp.GetRequiredService<ImapSyncService>());
        services.AddSingleton<IMailFolderAdminService, ImapFolderAdminService>();
        services.AddSingleton<IImapIdleWatcher, MailKitImapIdleWatcher>();
        services.AddSingleton<IMailDiagnosticsService, MailDiagnosticsService>();
        // 查看邮件源码：优先服务器原始 MIME，失败回退本地重建
        services.AddSingleton<IMailSourceService, MailSourceService>();
        services.AddSingleton<IMailSendService, SmtpSendService>();
        services.AddSingleton<IReadReceiptService, ReadReceiptService>();
        services.AddSingleton<IMailImportService, EmlImportService>();
        services.AddSingleton<IMailDraftService, MailDraftService>();
        services.AddSingleton<IOutboxProcessor, OutboxProcessor>();

        return services;
    }
}
