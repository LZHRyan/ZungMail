using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.Application.Services;

/// <summary>
/// 入库自动化：先按本地发件人黑白名单判定垃圾邮件，再执行用户规则。
/// 动作统一走 <see cref="MailActionService"/>，因此仍会进入待同步队列，不会绕过离线保证。
/// </summary>
public sealed class MailRuleService : IMailIngestHook
{
    private readonly IMailRuleRepository _rules;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly SenderFilterService _senderFilters;
    private readonly MailActionService _actions;
    private readonly MailTagService _tags;
    private readonly ILogger<MailRuleService> _logger;

    public MailRuleService(
        IMailRuleRepository rules,
        IFolderRepository folders,
        IMessageRepository messages,
        SenderFilterService senderFilters,
        MailActionService actions,
        MailTagService tags,
        ILogger<MailRuleService> logger)
    {
        _rules = rules;
        _folders = folders;
        _messages = messages;
        _senderFilters = senderFilters;
        _actions = actions;
        _tags = tags;
        _logger = logger;
    }

    public Task<IReadOnlyList<MailRule>> GetAllAsync(CancellationToken ct = default)
        => _rules.GetAllAsync(ct: ct);

    public Task<long> SaveAsync(MailRule rule, CancellationToken ct = default)
        => _rules.UpsertAsync(rule, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default)
        => _rules.DeleteAsync(id, ct);

    /// <summary>
    /// 对一批新入库邮件执行自动化：黑名单命中直接标记垃圾邮件（并跳过规则，避免被判为垃圾的邮件又被规则移出垃圾箱）；
    /// 其余邮件依次匹配规则，默认「命中即停」，勾选继续执行时才叠加后续规则。
    /// </summary>
    public async Task OnMessagesIngestedAsync(IReadOnlyList<EmailDetail> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return;

        IReadOnlyList<MailRule> rules;
        try
        {
            rules = await _rules.GetAllAsync(onlyEnabled: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 规则读不出来不影响垃圾邮件判定，因此不在这里 return。
            _logger.LogWarning(ex, "读取邮件规则失败，本轮跳过自动分类");
            rules = Array.Empty<MailRule>();
        }

        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();

            if (await ApplyBlacklistAsync(message, ct).ConfigureAwait(false)) continue;

            if (rules.Count == 0) continue;

            // 与 Foxmail 过滤器一致：标记类动作（星标/已读）不改变邮件归属，继续匹配后续规则；
            // 一旦执行了移动类动作（移动到文件夹/归档/垃圾邮件），后续规则不再处理这封邮件。
            // 规则勾选「命中后继续执行后续规则」时，任何动作都不打断后续匹配。
            foreach (var rule in rules)
            {
                if (!MailRuleMatch.Matches(rule, message)) continue;

                try
                {
                    await ApplyAsync(rule, message, ct).ConfigureAwait(false);
                    await _rules.RecordHitAsync(rule.Id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "规则 {Rule} 应用到邮件 {Id} 失败", rule.Name, message.Id);
                }

                if (!MailRuleFlow.ShouldContinueAfter(rule)) break;
            }
        }
    }

    // ---------------- 对已有邮件执行一次 ----------------

    /// <summary>对一封已存在的邮件执行这条规则（不经过「新邮件入库」入口）。</summary>
    public async Task<RuleRunResult> RunOnceAsync(
        MailRule rule,
        RuleRunScope scope,
        IProgress<RuleRunProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.HasCondition) return RuleRunResult.Empty(scope);

        var folders = RuleRunPlanner.SelectFolders(await _folders.GetAllAsync(ct).ConfigureAwait(false), scope);
        var limit = RuleRunPlanner.LimitFor(scope);

        var scanned = 0;
        var matched = 0;
        var failed = 0;

        progress?.Report(new RuleRunProgress(0, 0, folders.Count, "正在扫描…"));

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (scanned >= limit) break;

            var skip = 0;
            while (scanned < limit)
            {
                ct.ThrowIfCancellationRequested();

                var take = Math.Min(200, limit - scanned);
                var page = await _messages.QueryAsync(new MessageQuery
                {
                    FolderId = folder.Id,
                    Skip = skip,
                    Take = take
                }, ct).ConfigureAwait(false);

                if (page.Items.Count == 0) break;

                foreach (var message in page.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    scanned++;

                    if (!MailRuleMatch.Matches(rule, message.FromName, message.FromEmail, message.Subject)) continue;

                    try
                    {
                        await ApplyAsync(rule, message, ct).ConfigureAwait(false);
                        await _rules.RecordHitAsync(rule.Id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                        matched++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "规则 {Rule} 应用到已有邮件 {Id} 失败", rule.Name, message.Id);
                    }
                }

                progress?.Report(new RuleRunProgress(scanned, matched, folders.Count, folder.DisplayName ?? ""));

                if (page.Items.Count < take) break;
                skip += page.Items.Count;
            }
        }

        _logger.LogInformation(
            "规则 {Rule} 手动执行一次：扫描 {Scanned} 封、命中 {Matched} 封、失败 {Failed} 封（范围 {Scope}）",
            rule.Name, scanned, matched, failed, scope);

        return new RuleRunResult(scanned, matched, failed, folders.Count, scanned >= limit, scope);
    }

    /// <summary>黑名单命中则标记垃圾邮件并返回 true；白名单命中或未命中返回 false。</summary>
    private async Task<bool> ApplyBlacklistAsync(EmailDetail message, CancellationToken ct)
    {
        var verdict = await _senderFilters.ClassifyAsync(message.FromEmail, ct).ConfigureAwait(false);
        if (verdict != SenderVerdict.Blacklisted) return false;

        try
        {
            await _actions.MarkAsJunkAsync(message, ct).ConfigureAwait(false);
            _logger.LogInformation("发件人 {Sender} 命中本地黑名单，已标记为垃圾邮件", message.FromEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "本地黑名单应用到邮件 {Id} 失败", message.Id);
            return false;
        }
    }

    private async Task ApplyAsync(MailRule rule, EmailSummary message, CancellationToken ct)
    {
        switch (rule.Action)
        {
            case MailRuleAction.Star:
                await _actions.SetStarredAsync(message, true, ct).ConfigureAwait(false);
                break;

            case MailRuleAction.MarkSeen:
                await _actions.SetSeenAsync(message, true, ct).ConfigureAwait(false);
                break;

            case MailRuleAction.Archive:
                await _actions.ArchiveAsync(message, ct).ConfigureAwait(false);
                break;

            case MailRuleAction.MarkJunk:
                await _actions.MarkAsJunkAsync(message, ct).ConfigureAwait(false);
                break;

            case MailRuleAction.MoveToFolder when rule.TargetFolderId is { } folderId:
                var folder = await _folders.GetAsync(folderId, ct).ConfigureAwait(false);
                if (folder is null)
                {
                    _logger.LogWarning("规则 {Rule} 的目标文件夹 {FolderId} 已不存在", rule.Name, folderId);
                    return;
                }
                if (folder.AccountId != message.AccountId)
                {
                    _logger.LogWarning("规则 {Rule} 的目标文件夹属于其它账户，已跳过", rule.Name);
                    return;
                }
                await _actions.MoveToFolderAsync(message, folder, ct).ConfigureAwait(false);
                break;

            case MailRuleAction.AddTag:
                var tagName = (rule.ActionArg ?? string.Empty).Trim();
                if (tagName.Length == 0)
                {
                    _logger.LogWarning("规则 {Rule} 未指定标签，已跳过", rule.Name);
                    return;
                }

                var tags = await _tags.GetAllAsync(ct).ConfigureAwait(false);
                var tag = tags.FirstOrDefault(item =>
                    string.Equals(item.Name, tagName, StringComparison.OrdinalIgnoreCase));
                if (tag is null)
                {
                    _logger.LogWarning("规则 {Rule} 的标签 “{Tag}” 已不存在，已跳过", rule.Name, tagName);
                    return;
                }

                await _tags.AddToMessageAsync(message.Id, tag.Id, ct).ConfigureAwait(false);
                break;

            default:
                _logger.LogDebug("规则 {Rule} 没有可执行的动作", rule.Name);
                break;
        }
    }
}
