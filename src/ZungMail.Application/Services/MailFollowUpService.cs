using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ZungMail.Application.Services;

/// <summary>
/// 「等待回复」跟进服务：把「等客户补料 / 等船司放舱」变成可跟踪状态。
/// <para>
/// 判定规则：在同一个会话（thread）里、源邮件之后、发件人属于要求的收件人集合 → 视为该人已回复。
/// 满足条件（全部回复 / 至少 N 人）自动结束；否则到检查时间提醒一次，
/// 之后停在这一轮（不再重复轰炸），由用户决定「继续跟进」或「催信」。
/// </para>
/// </summary>
public sealed class MailFollowUpService
{
    /// <summary>检查时间快捷选项对应的时长（小时）。</summary>
    public static readonly IReadOnlyList<(int Hours, string Label)> CheckIntervals = new[]
    {
        (2, "2 小时后"),
        (4, "4 小时后"),
        (8, "8 小时后"),
        (24, "1 天后"),
        (48, "2 天后")
    };

    private readonly IMailFollowUpRepository _repository;
    private readonly ILogger<MailFollowUpService> _logger;

    public MailFollowUpService(IMailFollowUpRepository repository, ILogger<MailFollowUpService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public Task<MailFollowUp?> GetAsync(long id, CancellationToken ct = default)
        => _repository.GetAsync(id, ct);

    /// <summary>进行中的跟进，附带每个人的回复情况。</summary>
    public async Task<IReadOnlyList<FollowUpStatus>> GetActiveAsync(CancellationToken ct = default)
    {
        var items = await _repository.GetAllAsync(FollowUpState.Waiting, 100, ct).ConfigureAwait(false);
        var statuses = new List<FollowUpStatus>(items.Count);
        foreach (var item in items)
        {
            statuses.Add(await EvaluateAsync(item, ct).ConfigureAwait(false));
        }
        return statuses;
    }

    /// <summary>历史（已回复 / 已取消），不逐条评估。</summary>
    public async Task<IReadOnlyList<FollowUpStatus>> GetFinishedAsync(int take = 50, CancellationToken ct = default)
    {
        var waiting = await _repository.GetAllAsync(FollowUpState.Satisfied, take, ct).ConfigureAwait(false);
        var cancelled = await _repository.GetAllAsync(FollowUpState.Cancelled, take, ct).ConfigureAwait(false);

        return waiting.Concat(cancelled)
            .OrderByDescending(item => item.UpdatedUtc)
            .Take(take)
            .Select(item => new FollowUpStatus
            {
                FollowUp = item,
                Replied = item.State == FollowUpState.Satisfied ? item.Recipients : Array.Empty<string>(),
                Pending = item.State == FollowUpState.Satisfied ? Array.Empty<string>() : item.Recipients,
                IsSatisfied = item.State == FollowUpState.Satisfied
            })
            .ToList();
    }

    public async Task<FollowUpStatus?> GetStatusAsync(long id, CancellationToken ct = default)
    {
        var followUp = await _repository.GetAsync(id, ct).ConfigureAwait(false);
        return followUp is null ? null : await EvaluateAsync(followUp, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 创建跟进。<paramref name="recipients"/> 为需要回复的地址（已由界面勾选）。
    /// 同一封邮件只允许一条进行中的跟进，避免误建后重复提醒。
    /// </summary>
    public async Task<(bool Ok, string? Error, MailFollowUp? FollowUp)> CreateAsync(
        EmailDetail source,
        IReadOnlyList<string> recipients,
        bool requireAll,
        int minReplies,
        bool includeCc,
        DateTimeOffset nextCheckUtc,
        string note,
        string? selfAddress = null,
        DateTimeOffset? now = null,
        CancellationToken ct = default)
    {
        var normalized = MailFollowUp.NormalizeRecipients(recipients);
        if (!string.IsNullOrWhiteSpace(selfAddress))
        {
            normalized = normalized
                .Where(address => !string.Equals(address, selfAddress.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (normalized.Count == 0) return (false, "请至少选择一位需要回复的联系人。", null);

        var current = now ?? DateTimeOffset.Now;
        // 只有完全没给时间时才兜底；显式传入过去时间视为「立刻检查」，不要偷偷改成 4 小时后
        if (nextCheckUtc == default) nextCheckUtc = current.AddHours(4);

        var existing = await _repository.GetAllAsync(FollowUpState.Waiting, 200, ct).ConfigureAwait(false);
        if (existing.Any(item => item.EmailId == source.Id))
        {
            return (false, "这封邮件已有一条进行中的等待回复，请先在「等待回复」里处理。", null);
        }

        var followUp = new MailFollowUp
        {
            EmailId = source.Id,
            ThreadId = source.ThreadId ?? string.Empty,
            Subject = source.Subject ?? string.Empty,
            FromEmail = source.FromEmail ?? string.Empty,
            SentUtc = source.DateUtc,
            Recipients = normalized,
            RequireAll = requireAll,
            MinReplies = Math.Clamp(minReplies, 1, normalized.Count),
            IncludeCc = includeCc,
            NextCheckUtc = nextCheckUtc,
            State = FollowUpState.Waiting,
            Note = (note ?? string.Empty).Trim(),
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        await _repository.UpsertAsync(followUp, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "创建等待回复：邮件 {Email}，{Count} 位联系人，检查时间 {Next}",
            source.Id, normalized.Count, nextCheckUtc);

        return (true, null, followUp);
    }

    /// <summary>
    /// 检查所有到点的跟进：已满足条件的自动结束，其余返回给调用方提醒（并标记本轮已提醒）。
    /// </summary>
    public async Task<IReadOnlyList<FollowUpStatus>> CheckDueAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var due = await _repository.GetDueAsync(now, 20, ct).ConfigureAwait(false);
        if (due.Count == 0) return Array.Empty<FollowUpStatus>();

        var pendingNotifications = new List<FollowUpStatus>();
        foreach (var followUp in due)
        {
            var status = await EvaluateAsync(followUp, ct).ConfigureAwait(false);
            if (status.IsSatisfied) continue;

            // 一轮只提醒一次：标记后不再进入 GetDueAsync，直到用户「继续跟进」。
            await _repository.MarkNotifiedAsync(followUp.Id, now, ct).ConfigureAwait(false);
            pendingNotifications.Add(status);
        }

        return pendingNotifications;
    }

    /// <summary>继续跟进：重新排期并清空本轮提醒标记。</summary>
    public Task RescheduleAsync(long id, DateTimeOffset nextCheckUtc, CancellationToken ct = default)
        => _repository.RescheduleAsync(id, nextCheckUtc, DateTimeOffset.UtcNow, ct);

    public Task CancelAsync(long id, CancellationToken ct = default)
        => _repository.SetStateAsync(id, FollowUpState.Cancelled, DateTimeOffset.UtcNow, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) => _repository.DeleteAsync(id, ct);

    public Task<int> CountActiveAsync(CancellationToken ct = default) => _repository.CountActiveAsync(ct);

    /// <summary>
    /// 评估一条跟进。满足条件时顺带把状态改为「已回复」——
    /// 这样即使提醒任务没跑，用户打开列表也能看到自动结束。
    /// </summary>
    private async Task<FollowUpStatus> EvaluateAsync(MailFollowUp followUp, CancellationToken ct)
    {
        var replies = await _repository
            .GetThreadMessagesAfterAsync(followUp.ThreadId, followUp.EmailId, followUp.SentUtc, ct)
            .ConfigureAwait(false);

        var replied = new List<string>();
        var matched = new List<FollowUpReply>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reply in replies)
        {
            var address = (reply.FromEmail ?? string.Empty).Trim().ToLowerInvariant();
            if (address.Length == 0) continue;
            if (!followUp.Recipients.Contains(address, StringComparer.OrdinalIgnoreCase)) continue;
            if (!seen.Add(address)) continue;

            replied.Add(address);
            matched.Add(reply);
        }

        var pending = followUp.Recipients.Where(address => !seen.Contains(address)).ToList();
        var required = followUp.RequireAll
            ? followUp.Recipients.Count
            : Math.Clamp(followUp.MinReplies, 1, Math.Max(followUp.Recipients.Count, 1));
        var satisfied = replied.Count >= required && followUp.Recipients.Count > 0;

        if (satisfied && followUp.State == FollowUpState.Waiting)
        {
            try
            {
                await _repository.SetStateAsync(followUp.Id, FollowUpState.Satisfied, DateTimeOffset.UtcNow, ct)
                    .ConfigureAwait(false);
                followUp.State = FollowUpState.Satisfied;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "标记跟已完成失败：{Id}", followUp.Id);
            }
        }

        return new FollowUpStatus
        {
            FollowUp = followUp,
            Replies = matched,
            Replied = replied,
            Pending = pending,
            IsSatisfied = satisfied
        };
    }
}
