using System.IO;
using ZungMail.Core.Domain;
using ZungMail.Mail;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;
using ImapFlags = MailKit.MessageFlags;

namespace ZungMail.Tests;

/// <summary>
/// IMAP 会话确定性替身。共享一份 <see cref="FakeMailbox"/> 模拟服务器状态，
/// 通过 <see cref="FailOpenTimes"/> / <see cref="FailFetchTimes"/> 复现断线与协议异常。
/// </summary>
internal sealed class FakeImapClientFactory : IImapClientFactory
{
    private readonly FakeMailbox _mailbox;

    public FakeImapClientFactory(FakeMailbox mailbox) => _mailbox = mailbox;

    public int ConnectCount { get; private set; }

    public Task<IImapSession> ConnectAsync(Account account, CancellationToken ct = default)
    {
        ConnectCount++;
        return Task.FromResult<IImapSession>(new FakeImapSession(_mailbox));
    }
}

internal sealed class FakeMailbox
{
    public string FullName { get; init; } = "INBOX";

    public string Name { get; init; } = "收件箱";

    public uint UidValidity { get; init; } = 7;

    public bool SupportsUidPlus { get; init; }

    /// <summary>服务器上除主文件夹外还认识哪些完整路径（移动目标文件夹需要能解析）。</summary>
    public HashSet<string> KnownFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>批量 MOVE 命令的调用记录（每次调用一条 UID 集合）。</summary>
    public List<List<uint>> BatchMoves { get; } = new();

    public List<uint> Uids { get; } = new();

    public Dictionary<uint, ImapFlags> Flags { get; } = new();

    /// <summary>按 UID 指定邮件时间（缺省为当前时间），用于验证正文预取窗口。</summary>
    public Dictionary<uint, DateTimeOffset> Dates { get; } = new();

    /// <summary>按 UID 指定发件人地址（缺省 sender@example.com），用于陌生发件人判定。</summary>
    public Dictionary<uint, string> Senders { get; } = new();

    /// <summary>前 N 次打开文件夹抛出连接中断异常。</summary>
    public int FailOpenTimes { get; set; }

    /// <summary>前 N 次按 UID FETCH 抛出协议异常（模拟格式不合法响应）。</summary>
    public int FailFetchTimes { get; set; }

    public List<MessageSummaryItems> RequestedItems { get; } = new();

    public List<(int Start, int Count)> IndexWindows { get; } = new();

    public List<uint> Expunged { get; } = new();

    /// <summary>APPEND 进来的邮件（草稿 / 已发送归档）。</summary>
    public List<(MimeMessage Message, ImapFlags Flags)> Appended { get; } = new();

    /// <summary>APPEND 成功时返回的 UID；null 表示服务器不支持 UIDPLUS。</summary>
    public uint? AppendUid { get; set; } = 9001;

    /// <summary>按第几次 GetMessageAsync 调用（从 1 开始）决定返回或抛异常。</summary>
    public Func<int, MimeMessage>? MessageFactory { get; set; }

    /// <summary>按 UID 提供原始 MIME 字节（「查看邮件源码」用）；未配置时回退为序列化 <see cref="GetMessage"/>。</summary>
    public Dictionary<uint, byte[]> RawMessages { get; } = new();

    /// <summary>前 N 次取原始源码抛出协议异常（模拟服务器拒绝 BODY[]）。</summary>
    public int FailRawTimes { get; set; }

    /// <summary>取原始源码的 UID 记录。</summary>
    public List<uint> RawRequests { get; } = new();

    private int _openAttempts;
    private int _fetchCalls;
    private int _messageCalls;
    private int _rawCalls;

    public int MessageCalls => _messageCalls;

    public Task OpenAsync(CancellationToken ct)
    {
        _openAttempts++;
        if (_openAttempts <= FailOpenTimes) throw new IOException("模拟连接被服务端断开");
        return Task.CompletedTask;
    }

    public IReadOnlyList<RemoteMessageSummary> Fetch(IReadOnlyList<uint> uids, MessageSummaryItems items)
    {
        RequestedItems.Add(items);
        _fetchCalls++;
        if (_fetchCalls <= FailFetchTimes) throw new ImapProtocolException("模拟 FETCH 响应格式不合法");

        return uids
            .Select(uid => new RemoteMessageSummary
            {
                Uid = uid,
                Flags = Flags.TryGetValue(uid, out var flags) ? flags : ImapFlags.None,
                Size = 128,
                InternalDate = DateTimeOffset.UtcNow,
                PreviewText = "预览",
                Envelope = new Envelope
                {
                    Date = Dates.TryGetValue(uid, out var date) ? date : DateTimeOffset.UtcNow,
                    Subject = $"邮件 {uid}",
                    MessageId = $"<msg-{uid}@example.com>",
                    From = { new MailboxAddress("发件人", Senders.TryGetValue(uid, out var sender) ? sender : "sender@example.com") },
                    To = { new MailboxAddress("收件人", "me@example.com") }
                }
            })
            .ToList();
    }

    public IReadOnlyList<uint> Window(int start, int count)
    {
        IndexWindows.Add((start, count));
        return Uids.Skip(start).Take(count).ToList();
    }

    public MimeMessage GetMessage()
    {
        _messageCalls++;
        var factory = MessageFactory ?? (_ => BuildMessage());
        return factory(_messageCalls);
    }

    /// <summary>按 UID 返回原始 MIME 字节；未显式配置时序列化 <see cref="GetMessage"/> 的结果。</summary>
    public byte[] GetRaw(uint uid)
    {
        _rawCalls++;
        RawRequests.Add(uid);
        if (_rawCalls <= FailRawTimes) throw new ImapProtocolException("模拟服务器拒绝读取原始源码");

        if (RawMessages.TryGetValue(uid, out var configured)) return configured;

        using var message = GetMessage();
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        return buffer.ToArray();
    }

    public static MimeMessage BuildMessage(string text = "hello body")
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("发件人", "sender@example.com"));
        message.To.Add(new MailboxAddress("收件人", "me@example.com"));
        message.Subject = "正文测试";
        message.Body = new TextPart("plain") { Text = text };
        return message;
    }
}

internal sealed class FakeImapSession : IImapSession
{
    private readonly FakeMailbox _mailbox;
    private readonly FakeImapFolder _folder;

    public FakeImapSession(FakeMailbox mailbox)
    {
        _mailbox = mailbox;
        _folder = new FakeImapFolder(mailbox);
    }

    public bool IsConnected => true;

    public bool SupportsUidPlus => _mailbox.SupportsUidPlus;

    public bool SupportsMove => true;

    public bool SupportsImap4Rev2 => false;

    public bool Disposed { get; private set; }

    public Task<IReadOnlyList<IImapFolderSession>> GetFoldersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IImapFolderSession>>(new[] { _folder });

    public IImapFolderSession? TryGetFolder(string fullName) =>
        string.Equals(fullName, _mailbox.FullName, StringComparison.OrdinalIgnoreCase)
        || _mailbox.KnownFolders.Contains(fullName)
            ? _folder
            : null;

    public char DirectorySeparator => '/';

    public Task<IImapFolderSession> GetRootFolderAsync(CancellationToken ct = default)
        => Task.FromResult<IImapFolderSession>(_folder);

    public Task<IImapFolderSession> CreateFolderAsync(string name, CancellationToken ct = default)
        => Task.FromResult<IImapFolderSession>(_folder);

    public Task<string> RenameFolderAsync(string fullName, string newName, CancellationToken ct = default)
        => Task.FromResult(newName);

    public void Dispose() => Disposed = true;
}

internal sealed class FakeImapFolder : IImapFolderSession
{
    private readonly FakeMailbox _mailbox;

    public FakeImapFolder(FakeMailbox mailbox) => _mailbox = mailbox;

    public string Name => _mailbox.Name;

    public string FullName => _mailbox.FullName;

    public FolderAttributes Attributes => FolderAttributes.None;

    public int Count => _mailbox.Uids.Count;

    public int Unread => _mailbox.Uids.Count(uid =>
        !_mailbox.Flags.TryGetValue(uid, out var flags) || (flags & ImapFlags.Seen) == 0);

    public uint UidValidity => _mailbox.UidValidity;

    public Task OpenAsync(FolderAccess access, CancellationToken ct = default) => _mailbox.OpenAsync(ct);

    public Task CloseAsync(bool expunge, CancellationToken ct = default) => Task.CompletedTask;

    public Task RefreshStatusAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<RemoteMessageSummary>> FetchAsync(
        IReadOnlyList<uint> uids,
        MessageSummaryItems items,
        IReadOnlyList<HeaderId>? extraHeaders,
        CancellationToken ct = default) =>
        Task.FromResult(_mailbox.Fetch(uids, items));

    public Task<IReadOnlyList<uint>> FetchUidsByIndexAsync(int start, int count, CancellationToken ct = default) =>
        Task.FromResult(_mailbox.Window(start, count));

    public Task<MimeMessage> GetMessageAsync(uint uid, CancellationToken ct = default) =>
        Task.FromResult(_mailbox.GetMessage());

    public Task<byte[]> GetRawAsync(uint uid, CancellationToken ct = default) =>
        Task.FromResult(_mailbox.GetRaw(uid));

    public Task AddFlagsAsync(uint uid, ImapFlags flags, CancellationToken ct = default)
    {
        _mailbox.Flags[uid] = (_mailbox.Flags.TryGetValue(uid, out var current) ? current : ImapFlags.None) | flags;
        return Task.CompletedTask;
    }

    public Task RemoveFlagsAsync(uint uid, ImapFlags flags, CancellationToken ct = default)
    {
        _mailbox.Flags[uid] = (_mailbox.Flags.TryGetValue(uid, out var current) ? current : ImapFlags.None) & ~flags;
        return Task.CompletedTask;
    }

    public Task MoveToAsync(uint uid, IImapFolderSession destination, CancellationToken ct = default)
    {
        _mailbox.Expunged.Add(uid);
        return Task.CompletedTask;
    }

    public Task MoveManyAsync(IReadOnlyList<uint> uids, IImapFolderSession destination, CancellationToken ct = default)
    {
        _mailbox.BatchMoves.Add(uids.ToList());
        _mailbox.Expunged.AddRange(uids);
        return Task.CompletedTask;
    }

    public Task CopyToAsync(uint uid, IImapFolderSession destination, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task CopyManyAsync(IReadOnlyList<uint> uids, IImapFolderSession destination, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AddFlagsManyAsync(IReadOnlyList<uint> uids, ImapFlags flags, CancellationToken ct = default)
    {
        foreach (var uid in uids)
        {
            _mailbox.Flags[uid] = (_mailbox.Flags.TryGetValue(uid, out var current) ? current : ImapFlags.None) | flags;
        }

        return Task.CompletedTask;
    }

    public Task<IImapFolderSession> CreateChildAsync(string name, CancellationToken ct = default)
        => Task.FromResult<IImapFolderSession>(this);

    public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ExpungeAsync(IReadOnlyList<uint> uids, CancellationToken ct = default)
    {
        _mailbox.Expunged.AddRange(uids);
        return Task.CompletedTask;
    }

    public Task<uint?> AppendAsync(MimeMessage message, ImapFlags flags, CancellationToken ct = default)
    {
        _mailbox.Appended.Add((message, flags));
        return Task.FromResult(_mailbox.AppendUid);
    }
}
