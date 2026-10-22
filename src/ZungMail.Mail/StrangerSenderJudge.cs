using ZungMail.Core.Abstractions;
using ZungMail.Core.Services;

namespace ZungMail.Mail;

/// <summary>
/// 陌生发件人判定器：一轮同步共用一个实例，把「这个地址在第一封新邮件写入本地库之前是否已经认识」
/// 的结论缓存下来。
/// <para>
/// 缓存是必需的：判定必须在新邮件写库之前完成，而新邮件是逐 chunk 落库的；
/// 若每次都重新查库，同一批发件人的第二封（后面的 chunk）就会被刚写进去的第一封判成熟人，
/// 结果就是「一次发来三封只有一封带陌生标识」。
/// </para>
/// </summary>
internal sealed class StrangerSenderJudge
{
    private readonly IMessageRepository _messages;
    private readonly IReadOnlySet<string> _ownAddresses;
    private readonly IReadOnlySet<string> _ownDomains;
    private readonly Dictionary<string, bool> _decisions = new(StringComparer.Ordinal);

    public StrangerSenderJudge(IMessageRepository messages, IEnumerable<string?> ownAddresses)
    {
        _messages = messages;

        var addresses = ownAddresses as IReadOnlyCollection<string?> ?? ownAddresses.ToList();
        _ownAddresses = StrangerSenderPolicy.NormalizeAll(addresses);

        // 自有企业域名（已排除 qq / 163 之类公共邮箱域）：同域的同事即使地址没出现过，也不算陌生人
        _ownDomains = PhishingDomainPolicy.OwnDomains(addresses);
    }

    /// <summary>
    /// 为还没判定过的地址取一次库中快照结论：通讯录命中或历史来信命中即熟人。
    /// 结论写入后不再改变，因此同一轮里该地址的后续来信沿用同一个答案。
    /// </summary>
    public async Task DecideAsync(IEnumerable<string?> addresses, CancellationToken ct = default)
    {
        var pending = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var address in addresses)
        {
            var normalized = StrangerSenderPolicy.Normalize(address);
            if (normalized.Length == 0 || !seen.Add(normalized)) continue;
            if (_decisions.ContainsKey(normalized)) continue;

            // 自有账户地址直接算熟人，不必查库
            if (_ownAddresses.Contains(normalized))
            {
                _decisions[normalized] = false;
                continue;
            }

            // 与自有企业域名同域（例如同事用新地址第一次来信）：域名就是自家公司的，不按陌生人处理
            var domain = PhishingDomainPolicy.DomainOf(normalized);
            if (domain.Length > 0 && _ownDomains.Contains(domain))
            {
                _decisions[normalized] = false;
                continue;
            }

            pending.Add(normalized);
        }

        if (pending.Count == 0) return;

        var known = await _messages.GetKnownSenderAddressesAsync(pending, ct).ConfigureAwait(false);
        foreach (var address in pending)
        {
            _decisions[address] = StrangerSenderPolicy.IsStranger(address, known, _ownAddresses);
        }
    }

    /// <summary>取结论；从未判定过的地址按非陌生处理（例如空地址 / 已发送文件夹）。</summary>
    public bool IsStranger(string? address)
        => _decisions.TryGetValue(StrangerSenderPolicy.Normalize(address), out var stranger) && stranger;
}
