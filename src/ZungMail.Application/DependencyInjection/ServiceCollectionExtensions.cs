using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ZungMail.Application.DependencyInjection;

/// <summary>Application 层服务注册入口。WPF 组合根通过此扩展接入邮件流程协调服务。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册不依赖 WPF、SQLite 或 MailKit 具体类型的应用服务。
    /// 底层协议和仓储实现仍由宿主在此之前注册。
    /// </summary>
    public static IServiceCollection AddZungMailApplication(this IServiceCollection services)
    {
        services.AddSingleton<SyncExecutionGate>();
        services.AddSingleton<MailFolderService>();
        services.AddSingleton<SenderFilterService>();
        services.AddSingleton<MailRuleService>();
        services.AddSingleton<MailTagService>();
        services.AddSingleton<MailTodoService>();
        services.AddSingleton<MailFollowUpService>();
        services.AddSingleton<MailReferenceService>();
        services.AddSingleton<ShipmentPackageExporter>();
        services.AddSingleton<ContactExchangeService>();
        services.AddSingleton<IMailIngestHook, MailRuleIngestHook>();
        services.AddSingleton<IMailIngestHook, MailReferenceIngestHook>();
        services.AddSingleton<SynchronizationCoordinator>();

        // 每次解析一个实例：加载版本号是实例状态，主阅读区与独立阅读窗口必须各自独立，
        // 否则一边发起加载会把另一边的结果判为过期。
        services.AddTransient<MessageDetailLoader>();
        services.AddSingleton<MailActionService>();

        // 邮件摘要：规则抽取引擎零依赖、永远可用；本地小模型引擎由插件模块另行注册。
        services.AddSingleton<IMailSummaryProvider, RuleBasedMailSummarizer>();
        services.AddSingleton<IMailSummaryService, MailSummaryService>();
        return services;
    }
}
