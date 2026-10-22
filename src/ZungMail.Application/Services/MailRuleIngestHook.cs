using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace ZungMail.Application.Services;

/// <summary>
/// 规则执行钩子。同步引擎 → 钩子 → 规则服务 → <see cref="MailActionService"/> → 同步引擎
/// 会形成构造期循环依赖，因此这里延迟到消息真正入库后再从容器解析规则服务。
/// </summary>
public sealed class MailRuleIngestHook : IMailIngestHook
{
    private readonly IServiceProvider _provider;

    public MailRuleIngestHook(IServiceProvider provider) => _provider = provider;

    public Task OnMessagesIngestedAsync(IReadOnlyList<EmailDetail> messages, CancellationToken ct = default)
        => _provider.GetRequiredService<MailRuleService>().OnMessagesIngestedAsync(messages, ct);
}
