using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;

namespace ZungMail.Application.Services;

/// <summary>
/// 标签业务：按账户隔离（名称与键位只在同账户内唯一），
/// 并提供「按单键给选中邮件打 / 取消标签」的批量切换。
/// </summary>
public sealed class MailTagService
{
    private readonly IMailTagRepository _tags;

    public MailTagService(IMailTagRepository tags) => _tags = tags;

    /// <summary>标签新增、修改、删除后触发；供主窗口刷新快捷键缓存。</summary>
    public event EventHandler? TagsChanged;

    public Task<IReadOnlyList<MailTag>> GetAllAsync(CancellationToken ct = default)
        => _tags.GetAllAsync(ct);

    public Task<IReadOnlyList<MailTag>> GetByAccountAsync(long accountId, CancellationToken ct = default)
        => _tags.GetByAccountAsync(accountId, ct);

    /// <summary>同账户内按单键找标签；找不到返回 null。</summary>
    public static MailTag? ResolveByKey(IEnumerable<MailTag> tags, long accountId, string? key)
    {
        var normalized = TagShortcutKeys.Normalize(key);
        if (normalized is null) return null;

        foreach (var tag in tags)
        {
            if (tag.AccountId != accountId) continue;
            if (tag.MatchesShortcut(normalized)) return tag;
        }

        return null;
    }

    public async Task<(bool Ok, string? Error)> SaveAsync(MailTag tag, CancellationToken ct = default)
    {
        var name = (tag.Name ?? string.Empty).Trim();
        if (name.Length == 0) return (false, "标签名称不能为空。");
        if (name.Length > 20) return (false, "标签名称不能超过 20 个字符。");
        if (tag.AccountId <= 0) return (false, "请先选择标签所属账户。");

        if (!TagShortcutKeys.TryNormalize(tag.ShortcutKey, out var key, out var keyError))
        {
            return (false, keyError);
        }

        tag.ShortcutKey = key;

        // 名称与键位只在同一账户内查重：不同账户可以各有一套「异常 = 1」
        var existing = (await _tags.GetByAccountAsync(tag.AccountId, ct).ConfigureAwait(false))
            .Where(item => item.Id != tag.Id)
            .ToList();

        if (existing.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, $"该账户已有同名标签「{name}」。");
        }

        if (key is not null && existing.Any(item => item.MatchesShortcut(key)))
        {
            return (false, $"{key} 已被该账户的其它标签占用。");
        }

        tag.Name = name;
        await _tags.UpsertAsync(tag, ct).ConfigureAwait(false);
        TagsChanged?.Invoke(this, EventArgs.Empty);
        return (true, null);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await _tags.DeleteAsync(id, ct).ConfigureAwait(false);
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<MailTag>> GetForMessageAsync(long emailId, CancellationToken ct = default)
        => _tags.GetForMessageAsync(emailId, ct);

    public Task<IReadOnlyDictionary<long, IReadOnlyList<MailTag>>> GetForMessagesAsync(IReadOnlyList<long> emailIds, CancellationToken ct = default)
        => _tags.GetForMessagesAsync(emailIds, ct);

    public Task AddToMessageAsync(long emailId, long tagId, CancellationToken ct = default)
        => _tags.AddToMessageAsync(emailId, tagId, ct);

    /// <summary>批量打标签或批量移除。</summary>
    public async Task ApplyAsync(IReadOnlyList<long> emailIds, long tagId, bool add, CancellationToken ct = default)
    {
        foreach (var emailId in emailIds)
        {
            ct.ThrowIfCancellationRequested();
            if (add) await _tags.AddToMessageAsync(emailId, tagId, ct).ConfigureAwait(false);
            else await _tags.RemoveFromMessageAsync(emailId, tagId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 按单键给选中邮件「打标签 / 取消标签」：
    /// 每封邮件按自己所属账户找标签（不同账户可以用同一个键）；
    /// 选中的邮件全部已有该标签时统一移除，否则统一补上。
    /// </summary>
    public async Task<TagToggleResult> ToggleByShortcutAsync(
        IReadOnlyList<(long EmailId, long AccountId)> messages,
        string? key,
        CancellationToken ct = default)
    {
        if (messages.Count == 0) return TagToggleResult.Empty(key);

        var tags = await _tags.GetAllAsync(ct).ConfigureAwait(false);

        // 逐封解析：跨账户多选时每封邮件绑定的是自己账户的标签
        var targets = new List<(long EmailId, MailTag Tag)>(messages.Count);
        var skipped = 0;
        foreach (var (emailId, accountId) in messages)
        {
            var tag = ResolveByKey(tags, accountId, key);
            if (tag is null)
            {
                skipped++;
                continue;
            }

            targets.Add((emailId, tag));
        }

        if (targets.Count == 0) return TagToggleResult.Empty(key, skipped);

        var existing = await _tags
            .GetForMessagesAsync(targets.Select(t => t.EmailId).ToList(), ct)
            .ConfigureAwait(false);

        var applied = 0;
        var removed = 0;
        var tagNames = new List<string>();
        // 同一个键在不同账户可能是不同标签，按标签分组各判各的
        foreach (var group in targets.GroupBy(t => t.Tag.Id))
        {
            var tag = group.First().Tag;
            var ids = group.Select(t => t.EmailId).ToList();
            if (!tagNames.Contains(tag.Name)) tagNames.Add(tag.Name);

            var allTagged = ids.All(id =>
                existing.TryGetValue(id, out var ofMessage) && ofMessage.Any(item => item.Id == tag.Id));

            if (allTagged)
            {
                foreach (var id in ids)
                {
                    await _tags.RemoveFromMessageAsync(id, tag.Id, ct).ConfigureAwait(false);
                }

                removed += ids.Count;
            }
            else
            {
                foreach (var id in ids)
                {
                    await _tags.AddToMessageAsync(id, tag.Id, ct).ConfigureAwait(false);
                }

                applied += ids.Count;
            }
        }

        return new TagToggleResult(key, applied, removed, skipped, tagNames);
    }

    public Task<IReadOnlyList<long>> GetMessageIdsAsync(long tagId, CancellationToken ct = default)
        => _tags.GetMessageIdsAsync(tagId, ct);
}

/// <summary>一次「按单键打/取消标签」的结果：用于状态栏提示。</summary>
public sealed record TagToggleResult(string? Key, int Applied, int Removed, int Skipped, IReadOnlyList<string> TagNames)
{
    public bool Changed => Applied > 0 || Removed > 0;

    public static TagToggleResult Empty(string? key, int skipped = 0)
        => new(key, 0, 0, skipped, Array.Empty<string>());

    /// <summary>命中的标签名（跨账户可能不止一个）。</summary>
    private string TagLabel => TagNames.Count switch
    {
        0 => "",
        1 => $"「{TagNames[0]}」",
        _ => $"「{TagNames[0]}」等 {TagNames.Count} 个标签"
    };

    /// <summary>可直接显示在状态栏的一句话。</summary>
    public string Describe()
    {
        var key = string.IsNullOrWhiteSpace(Key) ? "" : Key;

        if (Changed)
        {
            var text = Applied > 0
                ? $"已给 {Applied} 封邮件打上{TagLabel}标签"
                : $"已从 {Removed} 封邮件取消{TagLabel}标签";
            if (Skipped > 0) text += $"（{Skipped} 封邮件的账户没有绑定 {key}）";
            return text;
        }

        return Skipped > 0
            ? $"选中的邮件里有 {Skipped} 封所属账户没有绑定 {key} 的标签"
            : $"当前账户没有绑定 {key} 的标签";
    }
}
