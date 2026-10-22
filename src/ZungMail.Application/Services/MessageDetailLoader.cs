using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;

namespace ZungMail.Application.Services;

/// <summary>
/// 阅读窗格详情加载：读取邮件详情、按需下载正文，并用版本号丢弃过期结果，
/// 避免快速切换邮件时旧请求覆盖新内容（NFR-P4）。
/// </summary>
public sealed class MessageDetailLoader
{
    private readonly IMessageRepository _messages;
    private readonly IMailSyncService _sync;
    private readonly MailReferenceService _references;
    private int _version;

    public MessageDetailLoader(IMessageRepository messages, IMailSyncService sync, MailReferenceService references)
    {
        _messages = messages;
        _sync = sync;
        _references = references;
    }

    /// <summary>开始一次新加载并返回版本号；此前未完成的加载结果都将被丢弃。</summary>
    public int Begin() => Interlocked.Increment(ref _version);

    /// <summary>本次加载是否仍是最新请求。</summary>
    public bool IsCurrent(int version) => Volatile.Read(ref _version) == version;

    /// <summary>
    /// 第一步：只读本地邮件头（主题 / 发件人 / 收件人 / 附件元数据），不做任何网络请求。
    /// headers-only 同步已把邮件头落库，所以这一步是纯本地查询，可以立刻上屏。
    /// 返回 null 表示邮件已不存在或本次加载已被更新的请求取代。
    /// </summary>
    public async Task<EmailDetail?> LoadHeaderAsync(long emailId, int version, CancellationToken ct = default)
    {
        var detail = await _messages.GetDetailAsync(emailId, ct).ConfigureAwait(false);
        if (detail is null || !IsCurrent(version)) return null;
        return detail;
    }

    /// <summary>
    /// 第二步：正文未下载时按需拉取，正文落地后重新读取详情（并补索引正文里的箱号/船名）。
    /// 下载失败时返回原邮件头，让界面保留「已读到的头 + 失败提示」，不要整页退回空态。
    /// </summary>
    public async Task<EmailDetail?> LoadBodyAsync(
        EmailDetail header, int version, Action<string>? status, CancellationToken ct = default)
    {
        if (header.BodyState == BodyState.Downloaded) return header;

        // 仅同步邮件头策略：选中时才按需下载正文
        status?.Invoke("正在下载正文…");
        var downloaded = await _sync.DownloadBodyAsync(header.Id, ct).ConfigureAwait(false);
        if (!IsCurrent(version)) return null;

        status?.Invoke(downloaded ? "就绪" : "正文下载失败，将在下次同步时重试");
        if (!downloaded) return header;

        var refreshed = await _messages.GetDetailAsync(header.Id, ct).ConfigureAwait(false);
        if (refreshed is not null && IsCurrent(version))
        {
            // 正文刚落地：把只出现在正文里的箱号、封号、船名航次补进业务标识索引
            await _references.IndexMessageAsync(refreshed, ct).ConfigureAwait(false);
        }

        return IsCurrent(version) ? refreshed ?? header : null;
    }

    /// <summary>
    /// 第三步（用户手动触发）：正文区为空时重拉正文。
    /// 与 <see cref="LoadBodyAsync"/> 的差别是<b>不看</b> <see cref="BodyState"/>：
    /// 出问题的邮件往往正是「已标记下载完成、但正文是空的」（上次解析失败 / 缓存被清 / 服务器返回空体），
    /// 按状态判断只会一直跳过，用户无从补救。这里强制再向服务器要一次并刷新详情。
    /// </summary>
    public async Task<EmailDetail?> ReloadBodyAsync(
        EmailDetail header, int version, Action<string>? status, CancellationToken ct = default)
    {
        status?.Invoke("正在重新获取正文…");
        var downloaded = await _sync.RedownloadBodyAsync(header.Id, ct).ConfigureAwait(false);
        if (!IsCurrent(version)) return null;

        status?.Invoke(downloaded ? "就绪" : "正文仍未取到，请检查网络或稍后再试");
        if (!downloaded) return header;

        var refreshed = await _messages.GetDetailAsync(header.Id, ct).ConfigureAwait(false);
        if (refreshed is not null && IsCurrent(version) && !string.IsNullOrWhiteSpace(refreshed.BodyText))
        {
            // 正文重新落地：把只出现在正文里的箱号、封号、船名航次补进业务标识索引
            await _references.IndexMessageAsync(refreshed, ct).ConfigureAwait(false);
        }

        return IsCurrent(version) ? refreshed ?? header : null;
    }
}
