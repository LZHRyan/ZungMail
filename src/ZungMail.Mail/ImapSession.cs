using ZungMail.Core.Domain;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;
using ImapFlags = MailKit.MessageFlags;

namespace ZungMail.Mail;

/// <summary>
/// 建立 IMAP 会话的工厂。抽出这一层后，<see cref="ImapSyncService"/> 不再直接依赖 MailKit 的连接类型，
/// 可用确定性替身覆盖断线重连、协议异常、UIDVALIDITY 变化等难以用真实服务器复现的场景。
/// </summary>
public interface IImapClientFactory
{
    /// <summary>连接并完成认证；调用方负责释放返回的会话。</summary>
    Task<IImapSession> ConnectAsync(Account account, CancellationToken ct = default);
}

/// <summary>一次已认证的 IMAP 连接。</summary>
public interface IImapSession : IDisposable
{
    bool IsConnected { get; }

    /// <summary>服务器是否支持 UIDPLUS（可用于按 UID 精确 EXPUNGE）。</summary>
    bool SupportsUidPlus { get; }

    /// <summary>服务器是否支持 RFC 6851 MOVE。</summary>
    bool SupportsMove { get; }

    bool SupportsImap4Rev2 { get; }

    /// <summary>枚举可访问的文件夹；无个人命名空间时返回空集合。</summary>
    Task<IReadOnlyList<IImapFolderSession>> GetFoldersAsync(CancellationToken ct = default);

    /// <summary>按完整名称取文件夹；服务器上不存在时返回 <c>null</c>。</summary>
    IImapFolderSession? TryGetFolder(string fullName);

    /// <summary>IMAP 层级分隔符；服务器未声明时回退为 '/'</summary>
    char DirectorySeparator { get; }

    /// <summary>个人命名空间的根文件夹；用于 CREATE 等需要根句柄的操作。</summary>
    Task<IImapFolderSession> GetRootFolderAsync(CancellationToken ct = default);

    /// <summary>在根命名空间下创建文件夹。</summary>
    Task<IImapFolderSession> CreateFolderAsync(string name, CancellationToken ct = default);

    /// <summary>重命名文件夹并返回新的完整路径。</summary>
    Task<string> RenameFolderAsync(string fullName, string newName, CancellationToken ct = default);
}

/// <summary>远端文件夹：选择、抓取摘要与正文、回写标志位。</summary>
public interface IImapFolderSession
{
    string Name { get; }

    string FullName { get; }

    FolderAttributes Attributes { get; }

    int Count { get; }

    int Unread { get; }

    uint UidValidity { get; }

    Task OpenAsync(FolderAccess access, CancellationToken ct = default);

    Task CloseAsync(bool expunge, CancellationToken ct = default);

    /// <summary>刷新 <see cref="Count"/> / <see cref="Unread"/>（对应 IMAP STATUS）。</summary>
    Task RefreshStatusAsync(CancellationToken ct = default);

    /// <summary>按 UID 抓取摘要，返回顺序与服务器一致。</summary>
    Task<IReadOnlyList<RemoteMessageSummary>> FetchAsync(
        IReadOnlyList<uint> uids,
        MessageSummaryItems items,
        IReadOnlyList<HeaderId>? extraHeaders,
        CancellationToken ct = default);

    /// <summary>按序位区间（<c>FETCH start:count (UID)</c>）读取 UID，返回升序列表。</summary>
    Task<IReadOnlyList<uint>> FetchUidsByIndexAsync(int start, int count, CancellationToken ct = default);

    Task<MimeMessage> GetMessageAsync(uint uid, CancellationToken ct = default);

    Task AddFlagsAsync(uint uid, ImapFlags flags, CancellationToken ct = default);

    Task RemoveFlagsAsync(uint uid, ImapFlags flags, CancellationToken ct = default);

    /// <summary>原生 MOVE：将指定 UID 移入目标文件夹。</summary>
    Task MoveToAsync(uint uid, IImapFolderSession destination, CancellationToken ct = default);

    /// <summary>原生 MOVE 批量版：一条 <c>UID MOVE uid-set</c> 带走整批，省掉逐封的命令往返。</summary>
    Task MoveManyAsync(IReadOnlyList<uint> uids, IImapFolderSession destination, CancellationToken ct = default);

    /// <summary>MOVE 不可用时的 COPY 回退。</summary>
    Task CopyToAsync(uint uid, IImapFolderSession destination, CancellationToken ct = default);

    /// <summary>COPY 回退的批量版。</summary>
    Task CopyManyAsync(IReadOnlyList<uint> uids, IImapFolderSession destination, CancellationToken ct = default);

    /// <summary>批量为多个 UID 加同一个标志（一条 <c>UID STORE</c>）。</summary>
    Task AddFlagsManyAsync(IReadOnlyList<uint> uids, ImapFlags flags, CancellationToken ct = default);

    /// <summary>按 UID 精确 EXPUNGE（需服务器支持 UIDPLUS）。</summary>
    Task ExpungeAsync(IReadOnlyList<uint> uids, CancellationToken ct = default);

    /// <summary>追加邮件到当前文件夹，返回服务器分配的 UID（不支持 UIDPLUS 时为 null）。</summary>
    Task<uint?> AppendAsync(MimeMessage message, ImapFlags flags, CancellationToken ct = default);

    /// <summary>创建子文件夹并返回新文件夹句柄。</summary>
    Task<IImapFolderSession> CreateChildAsync(string name, CancellationToken ct = default);

    /// <summary>删除当前文件夹（服务器通常拒绝删除非空文件夹）。</summary>
    Task DeleteAsync(CancellationToken ct = default);

    /// <summary>
    /// 按 UID 取回**原始 MIME 字节**（<c>UID FETCH BODY[]</c>），供「查看邮件源码」使用。
    /// 与 <see cref="GetMessageAsync"/> 的区别：这里不经过 MailKit 解析/重排，字节与服务器上一致。
    /// </summary>
    Task<byte[]> GetRawAsync(uint uid, CancellationToken ct = default);
}

/// <summary>与协议库解耦的邮件摘要：由 MailKit 适配层映射而来，测试替身可直接构造。</summary>
public sealed class RemoteMessageSummary
{
    public uint Uid { get; init; }

    public ImapFlags? Flags { get; init; }

    public long? Size { get; init; }

    public DateTimeOffset? InternalDate { get; init; }

    public string? PreviewText { get; init; }

    public bool HasAttachment { get; init; }

    public Envelope? Envelope { get; init; }

    /// <summary>Disposition-Notification-To 头（阅读收条请求）。</summary>
    public string? MdnTo { get; init; }

    /// <summary>References 头（会话归并需要，Envelope 不含）。</summary>
    public string? References { get; init; }

    /// <summary>
    /// BODYSTRUCTURE 里的顶层结构是 <c>multipart/report; report-type=delivery-status</c>：
    /// 这是标准退信（RFC 3464 DSN）的结构信号，比「发件人 + 主题」可靠。
    /// </summary>
    public bool IsDsnReport { get; init; }
}

/// <summary>默认实现：通过 <see cref="ImapConnector"/> 建立真实的 MailKit 连接。</summary>
public sealed class MailKitImapClientFactory : IImapClientFactory
{
    public async Task<IImapSession> ConnectAsync(Account account, CancellationToken ct = default) =>
        new MailKitImapSession(await ImapConnector.ConnectAsync(account, ct).ConfigureAwait(false));
}

internal sealed class MailKitImapSession : IImapSession
{
    private readonly ImapClient _client;

    public MailKitImapSession(ImapClient client) => _client = client;

    public bool IsConnected => _client.IsConnected;

    public bool SupportsUidPlus => _client.Capabilities.HasFlag(ImapCapabilities.UidPlus);

    public bool SupportsMove => _client.Capabilities.HasFlag(ImapCapabilities.Move);

    public bool SupportsImap4Rev2 => _client.Capabilities.HasFlag(ImapCapabilities.IMAP4rev2);

    public async Task<IReadOnlyList<IImapFolderSession>> GetFoldersAsync(CancellationToken ct = default)
    {
        if (_client.PersonalNamespaces.Count == 0) return Array.Empty<IImapFolderSession>();

        var folders = await _client
            .GetFoldersAsync(_client.PersonalNamespaces[0], StatusItems.None, false, ct)
            .ConfigureAwait(false);

        return folders.Select(folder => (IImapFolderSession)new MailKitImapFolder(folder)).ToList();
    }

    public IImapFolderSession? TryGetFolder(string fullName)
    {
        try
        {
            return new MailKitImapFolder(_client.GetFolder(fullName));
        }
        catch (FolderNotFoundException)
        {
            return null;
        }
    }

    public char DirectorySeparator =>
        _client.PersonalNamespaces.FirstOrDefault()?.DirectorySeparator ?? '/';

    public Task<IImapFolderSession> GetRootFolderAsync(CancellationToken ct = default)
    {
        // 个人命名空间的根句柄是 CREATE 的可靠落点；QQ / 263 等服务器要求在此基础上建文件夹。
        var personal = _client.PersonalNamespaces.FirstOrDefault();
        IImapFolderSession root = personal is null
            ? new MailKitImapFolder((IMailFolder)_client)
            : new MailKitImapFolder(_client.GetFolder(personal));
        return Task.FromResult(root);
    }

    public async Task<IImapFolderSession> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        var root = await GetRootFolderAsync(ct).ConfigureAwait(false);
        return await root.CreateChildAsync(name, ct).ConfigureAwait(false);
    }

    public async Task<string> RenameFolderAsync(string fullName, string newName, CancellationToken ct = default)
    {
        var folder = _client.GetFolder(fullName);
        IMailFolder parent = folder.ParentFolder ?? (IMailFolder)_client;
        await parent.RenameAsync(folder, newName, ct).ConfigureAwait(false);
        var separator = folder.ParentFolder is null ? string.Empty : DirectorySeparator.ToString();
        return folder.ParentFolder is null
            ? newName
            : $"{folder.ParentFolder.FullName}{separator}{newName}";
    }

    public void Dispose() => _client.Dispose();
}

internal sealed class MailKitImapFolder : IImapFolderSession
{
    private readonly IMailFolder _folder;

    public MailKitImapFolder(IMailFolder folder) => _folder = folder;

    public string Name => _folder.Name;

    public string FullName => _folder.FullName;

    public FolderAttributes Attributes => _folder.Attributes;

    public int Count => _folder.Count;

    public int Unread => _folder.Unread;

    public uint UidValidity => _folder.UidValidity;

    public Task OpenAsync(FolderAccess access, CancellationToken ct = default) =>
        _folder.OpenAsync(access, ct);

    public Task CloseAsync(bool expunge, CancellationToken ct = default) =>
        _folder.CloseAsync(expunge, ct);

    public Task RefreshStatusAsync(CancellationToken ct = default) =>
        _folder.StatusAsync(StatusItems.Count | StatusItems.Unread, ct);

    public async Task<IReadOnlyList<RemoteMessageSummary>> FetchAsync(
        IReadOnlyList<uint> uids,
        MessageSummaryItems items,
        IReadOnlyList<HeaderId>? extraHeaders,
        CancellationToken ct = default)
    {
        if (uids.Count == 0) return Array.Empty<RemoteMessageSummary>();

        var ids = uids.Select(uid => new UniqueId(uid)).ToList();
        // MailKit 的「带附加头」重载要求非空集合；null 时改走不带头的重载，避免 ArgumentNullException。
        var summaries = extraHeaders is { Count: > 0 }
            ? await _folder.FetchAsync(ids, items, extraHeaders, ct).ConfigureAwait(false)
            : await _folder.FetchAsync(ids, items, ct).ConfigureAwait(false);

        return summaries.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<uint>> FetchUidsByIndexAsync(int start, int count, CancellationToken ct = default)
    {
        // 调用方给的是「0 基偏移 + 数量」，MailKit 的 FetchAsync(min,max) 是「1 基闭区间」；
        // 换算集中到 UidWindow，边界行为由单元测试守护（曾因混用基准导致所有文件夹同步失败）。
        if (UidWindow.ToFetchRange(start, count, _folder.Count) is not { } range)
        {
            return Array.Empty<uint>();
        }

        var (first, last) = range;

        IList<IMessageSummary> summaries;
        try
        {
            summaries = await _folder
                .FetchAsync(first, last, MessageSummaryItems.UniqueId, ct)
                .ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Array.Empty<uint>();
        }

        return summaries
            .Where(summary => summary.UniqueId.IsValid)
            .OrderBy(summary => summary.Index)
            .Select(summary => summary.UniqueId.Id)
            .ToList();
    }

    public Task<MimeMessage> GetMessageAsync(uint uid, CancellationToken ct = default) =>
        _folder.GetMessageAsync(new UniqueId(uid), ct);

    public async Task<byte[]> GetRawAsync(uint uid, CancellationToken ct = default)
    {
        await using var stream = await _folder.GetStreamAsync(new UniqueId(uid), ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public Task AddFlagsAsync(uint uid, ImapFlags flags, CancellationToken ct = default) =>
        _folder.AddFlagsAsync(new UniqueId(uid), flags, true, ct);

    public Task RemoveFlagsAsync(uint uid, ImapFlags flags, CancellationToken ct = default) =>
        _folder.RemoveFlagsAsync(new UniqueId(uid), flags, true, ct);

    public Task MoveToAsync(uint uid, IImapFolderSession destination, CancellationToken ct = default)
    {
        var target = destination as MailKitImapFolder
            ?? throw new ArgumentException("目标文件夹必须属于同一 MailKit 会话", nameof(destination));
        return _folder.MoveToAsync(new UniqueId(uid), target._folder, ct);
    }

    public Task MoveManyAsync(IReadOnlyList<uint> uids, IImapFolderSession destination, CancellationToken ct = default)
    {
        var target = destination as MailKitImapFolder
            ?? throw new ArgumentException("目标文件夹必须属于同一 MailKit 会话", nameof(destination));
        var set = uids.Select(uid => new UniqueId(uid)).ToList();
        return _folder.MoveToAsync(set, target._folder, ct);
    }

    public Task CopyToAsync(uint uid, IImapFolderSession destination, CancellationToken ct = default)
    {
        var target = destination as MailKitImapFolder
            ?? throw new ArgumentException("目标文件夹必须属于同一 MailKit 会话", nameof(destination));
        return _folder.CopyToAsync(new UniqueId(uid), target._folder, ct);
    }

    public Task CopyManyAsync(IReadOnlyList<uint> uids, IImapFolderSession destination, CancellationToken ct = default)
    {
        var target = destination as MailKitImapFolder
            ?? throw new ArgumentException("目标文件夹必须属于同一 MailKit 会话", nameof(destination));
        var set = uids.Select(uid => new UniqueId(uid)).ToList();
        return _folder.CopyToAsync(set, target._folder, ct);
    }

    public Task AddFlagsManyAsync(IReadOnlyList<uint> uids, ImapFlags flags, CancellationToken ct = default)
    {
        var set = uids.Select(uid => new UniqueId(uid)).ToList();
        return _folder.AddFlagsAsync(set, flags, true, ct);
    }

    public async Task ExpungeAsync(IReadOnlyList<uint> uids, CancellationToken ct = default) =>
        await _folder
            .ExpungeAsync(uids.Select(uid => new UniqueId(uid)).ToList(), ct)
            .ConfigureAwait(false);

    public async Task<uint?> AppendAsync(MimeMessage message, ImapFlags flags, CancellationToken ct = default)
    {
        var uid = await _folder.AppendAsync(message, flags, ct).ConfigureAwait(false);
        return uid?.Id;
    }

    public async Task<IImapFolderSession> CreateChildAsync(string name, CancellationToken ct = default)
    {
        // MailKit 的 CreateAsync 只接受不含层级分隔符的「叶子名称」，
        // 由父文件夹句柄负责补全完整路径；不能自行拼接 "父/子"。
        var created = await _folder.CreateAsync(name, true, ct).ConfigureAwait(false);
        if (created is null)
        {
            // 新版 MailKit 的 CreateAsync 可能返回 null（服务器没回该文件夹），不能把 null 当句柄用
            throw new InvalidOperationException($"创建文件夹「{name}」失败：服务器未返回该文件夹。");
        }

        return new MailKitImapFolder(created);
    }

    public Task DeleteAsync(CancellationToken ct = default) => _folder.DeleteAsync(ct);

    private static RemoteMessageSummary Map(IMessageSummary summary) => new()
    {
        Uid = summary.UniqueId.Id,
        Flags = summary.Flags,
        Size = summary.Size,
        InternalDate = summary.InternalDate,
        PreviewText = summary.PreviewText,
        HasAttachment = HasAttachment(summary),
        Envelope = summary.Envelope,
        References = summary.Headers?[HeaderId.References],
        MdnTo = summary.Headers?[HeaderId.DispositionNotificationTo],
        IsDsnReport = IsDsnReport(summary)
    };

    /// <summary>
    /// 邮件结构里带 <c>message/delivery-status</c> 段时是退信报告（RFC 3464 DSN）。
    /// 不能只看顶层 <c>multipart/report</c>：阅读收条（MDN）也是 multipart/report，
    /// 但里面的段是 <c>message/disposition-notification</c>，必须靠段类型区分。
    /// BODYSTRUCTURE 缺失或格式异常一律按「不是」处理，退信判定会走发件人 + 主题兜底。
    /// </summary>
    private static bool IsDsnReport(IMessageSummary summary)
    {
        try
        {
            return ContainsDeliveryStatus(summary.Body);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool ContainsDeliveryStatus(BodyPart? part) => part switch
    {
        BodyPartMultipart multipart => multipart.BodyParts.Any(ContainsDeliveryStatus),
        BodyPartMessage nested => ContainsDeliveryStatus(nested.Body),
        null => false,
        _ => string.Equals(part.ContentType?.MimeType, "message/delivery-status", StringComparison.OrdinalIgnoreCase)
    };

    /// <summary>BODYSTRUCTURE 缺失或格式异常时不影响同步，仅失去「是否有附件」的判定。</summary>
    private static bool HasAttachment(IMessageSummary summary)
    {
        if (summary.Body is null) return false;
        try
        {
            return summary.Attachments.Any();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
