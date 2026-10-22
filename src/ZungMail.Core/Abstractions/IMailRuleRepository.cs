using ZungMail.Core.Domain;

namespace ZungMail.Core.Abstractions;

/// <summary>规则持久化：条件、动作与命中统计。</summary>
public interface IMailRuleRepository
{
    Task<IReadOnlyList<MailRule>> GetAllAsync(bool onlyEnabled = false, CancellationToken ct = default);

    Task<MailRule?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>插入或更新，返回主键。</summary>
    Task<long> UpsertAsync(MailRule rule, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>记录一次命中（次数 +1，更新时间）。</summary>
    Task RecordHitAsync(long id, DateTimeOffset hitUtc, CancellationToken ct = default);
}

/// <summary>
/// 邮件入库后的后置钩子。同步引擎在写入新邮件后调用，用于规则、自动分类等本地自动化，
/// 避免协议层直接依赖 Application 层的业务服务。
/// </summary>
public interface IMailIngestHook
{
    Task OnMessagesIngestedAsync(IReadOnlyList<EmailDetail> messages, CancellationToken ct = default);

    /// <summary>
    /// 正文（与附件）落地后的后置处理：用于补齐只出现在正文里的本地索引（如箱号 / 船名）。
    /// 大多数钩子只关心邮件头，默认不处理。
    /// </summary>
    Task OnBodiesDownloadedAsync(IReadOnlyList<EmailDetail> messages, CancellationToken ct = default)
        => Task.CompletedTask;
}
