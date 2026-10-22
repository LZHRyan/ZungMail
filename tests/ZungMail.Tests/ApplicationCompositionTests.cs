using System.Reflection;
using ZungMail.Application.DependencyInjection;
using ZungMail.Application.Services;
using ZungMail.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ZungMail.Tests;

/// <summary>验证 Application 层的组合根只注册应用服务，且可由宿主提供的端口完成装配。</summary>
[Trait("Category", "Unit")]
public sealed class ApplicationCompositionTests
{
    [Fact]
    public void AddZungMailApplication_可解析所有协调服务且保持单例()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(_ => CreateProxy<IMessageRepository>());
        services.AddSingleton<IPendingOperationRepository>(_ => CreateProxy<IPendingOperationRepository>());
        services.AddSingleton<IMailSyncService>(_ => CreateProxy<IMailSyncService>());
        services.AddSingleton<IMailFolderAdminService>(_ => CreateProxy<IMailFolderAdminService>());
        services.AddSingleton<IFolderRepository>(_ => CreateProxy<IFolderRepository>());
        services.AddSingleton<IMailRuleRepository>(_ => CreateProxy<IMailRuleRepository>());
        services.AddSingleton<ISenderFilterRepository>(_ => CreateProxy<ISenderFilterRepository>());
        services.AddSingleton<IMailTagRepository>(_ => CreateProxy<IMailTagRepository>());
        services.AddSingleton<IMailTodoRepository>(_ => CreateProxy<IMailTodoRepository>());
        services.AddSingleton<IMailReferenceRepository>(_ => CreateProxy<IMailReferenceRepository>());
        services.AddSingleton<IShipmentRepository>(_ => CreateProxy<IShipmentRepository>());
        services.AddSingleton<IMailFollowUpRepository>(_ => CreateProxy<IMailFollowUpRepository>());
        services.AddSingleton<IContactRepository>(_ => CreateProxy<IContactRepository>());
        services.AddSingleton<ILogger<SynchronizationCoordinator>>(NullLogger<SynchronizationCoordinator>.Instance);
        services.AddSingleton<ILogger<MailActionService>>(NullLogger<MailActionService>.Instance);
        services.AddSingleton<ILogger<MailFolderService>>(NullLogger<MailFolderService>.Instance);
        services.AddSingleton<ILogger<SenderFilterService>>(NullLogger<SenderFilterService>.Instance);
        services.AddSingleton<ILogger<MailRuleService>>(NullLogger<MailRuleService>.Instance);
        services.AddSingleton<ILogger<MailReferenceService>>(NullLogger<MailReferenceService>.Instance);
        services.AddSingleton<ILogger<MailFollowUpService>>(NullLogger<MailFollowUpService>.Instance);
        services.AddSingleton<ILogger<ContactExchangeService>>(NullLogger<ContactExchangeService>.Instance);
        services.AddZungMailApplication();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        AssertSingleton<SynchronizationCoordinator>(provider);

        // 详情加载器按消费者分配：主阅读区与独立阅读窗口各自持有加载版本号，互不打断
        Assert.NotSame(
            provider.GetRequiredService<MessageDetailLoader>(),
            provider.GetRequiredService<MessageDetailLoader>());

        AssertSingleton<MailActionService>(provider);
        AssertSingleton<MailFolderService>(provider);
        AssertSingleton<SenderFilterService>(provider);
        AssertSingleton<MailRuleService>(provider);
        AssertSingleton<MailReferenceService>(provider);
        AssertSingleton<ContactExchangeService>(provider);
    }

    private static void AssertSingleton<T>(IServiceProvider provider) where T : class
    {
        Assert.Same(provider.GetRequiredService<T>(), provider.GetRequiredService<T>());
    }

    private static T CreateProxy<T>() where T : class
        => DispatchProxy.Create<T, NoopInterfaceProxy>();

    private class NoopInterfaceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
