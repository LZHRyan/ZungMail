using System.Text;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;

namespace ZungMail.Mail;

/// <summary>
/// 「查看邮件源码」的数据来源：优先连 IMAP 取服务器上的原始 MIME（<c>UID FETCH BODY[]</c>），
/// 取不到时回退为按本地库字段重建的邮件（与「作为附件转发」共用 <see cref="MailEmlExporter"/>）。
/// <para>
/// 约定：只读、不写库、不改标志位。源码是一次性的排障视图，落盘只会让数据目录继续膨胀。
/// </para>
/// </summary>
public sealed class MailSourceService : IMailSourceService
{
    /// <summary>窗口里最多显示多少字节，超出截断（另存仍写完整字节）。</summary>
    internal const int MaxDisplayBytes = 4 * 1024 * 1024;

    /// <summary>原始源码上限：再大就不在窗口里展示，提示改用「作为附件转发」导出。</summary>
    internal const long MaxSourceBytes = 32L * 1024 * 1024;

    private readonly IMessageRepository _messages;
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IMailSyncService _sync;
    private readonly IImapClientFactory _clientFactory;
    private readonly ILogger<MailSourceService> _logger;

    public MailSourceService(
        IMessageRepository messages,
        IAccountRepository accounts,
        IFolderRepository folders,
        IMailSyncService sync,
        IImapClientFactory clientFactory,
        ILogger<MailSourceService> logger)
    {
        _messages = messages;
        _accounts = accounts;
        _folders = folders;
        _sync = sync;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public async Task<MailSourceSnapshot> LoadAsync(
        long messageId,
        bool preferServerSource = true,
        CancellationToken ct = default)
    {
        var detail = await _messages.GetDetailAsync(messageId, ct).ConfigureAwait(false)
                     ?? throw new MailSourceUnavailableException("邮件已不存在，可能已被删除。");

        string? fallbackReason = null;

        if (preferServerSource)
        {
            try
            {
                var raw = await FetchRawAsync(detail, ct).ConfigureAwait(false);
                return CreateSnapshot(detail.Id, MailSourceKind.ServerRaw, raw, fallbackReason: null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (MailSourceUnavailableException)
            {
                // 体积超限这类「重试也没用」的结论直接抛给界面，不做无意义的回退
                throw;
            }
            catch (Exception ex)
            {
                fallbackReason = DescribeFailure(ex);
                _logger.LogInformation(ex, "读取原始源码失败，回退本地重建：邮件 {Id}", messageId);
            }
        }

        // 只有邮件头时先补一次正文，否则重建出来的邮件没有内容
        if (!detail.HasBody)
        {
            try
            {
                var downloaded = await _sync.DownloadBodyAsync(detail.Id, ct).ConfigureAwait(false);
                if (downloaded)
                {
                    detail = await _messages.GetDetailAsync(detail.Id, ct).ConfigureAwait(false) ?? detail;
                }
                else
                {
                    fallbackReason = AppendReason(fallbackReason, "正文下载失败");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                fallbackReason = AppendReason(fallbackReason, "正文下载失败（" + DescribeFailure(ex) + "）");
                _logger.LogInformation(ex, "为查看源码补下正文失败：邮件 {Id}", messageId);
            }
        }

        if (!detail.HasBody)
        {
            throw new MailSourceUnavailableException(
                "无法获取源码：" + (fallbackReason ?? "服务器未返回原始源码") + "，且本地没有正文可重建。请连接网络后重试。");
        }

        byte[] rebuilt;
        await using (var buffer = new MemoryStream())
        {
            await MailEmlExporter.Build(detail).WriteToAsync(buffer, ct).ConfigureAwait(false);
            rebuilt = buffer.ToArray();
        }

        return CreateSnapshot(detail.Id, MailSourceKind.Rebuilt, rebuilt, fallbackReason);
    }

    /// <summary>连一次只读会话取原始字节；不改服务器标志位，也不与同步会话共用连接。</summary>
    private async Task<byte[]> FetchRawAsync(EmailDetail detail, CancellationToken ct)
    {
        var account = await _accounts.GetAsync(detail.AccountId, ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("找不到该邮件的账户配置。");
        var folder = await _folders.GetAsync(detail.FolderId, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("找不到该邮件所在的文件夹。");

        using var session = await _clientFactory.ConnectAsync(account, ct).ConfigureAwait(false);
        var remote = session.TryGetFolder(folder.FullName)
                     ?? throw new FolderNotFoundException("服务器上找不到邮件所在文件夹", folder.FullName);

        await remote.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        try
        {
            var bytes = await remote.GetRawAsync(detail.Uid, ct).ConfigureAwait(false);
            if (bytes.Length > MaxSourceBytes)
            {
                throw new MailSourceUnavailableException(
                    $"邮件源码 {bytes.Length / (1024d * 1024d):F1} MB，超过 {MaxSourceBytes / (1024 * 1024)} MB 上限。" +
                    "请改用「作为附件转发」导出 .eml。");
            }

            if (bytes.Length == 0)
            {
                throw new InvalidOperationException("服务器返回了空内容。");
            }

            return bytes;
        }
        finally
        {
            await remote.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static MailSourceSnapshot CreateSnapshot(
        long messageId, MailSourceKind kind, byte[] bytes, string? fallbackReason)
    {
        var (text, encodingName) = DecodeForDisplay(bytes, out var truncated);

        return new MailSourceSnapshot
        {
            MessageId = messageId,
            Kind = kind,
            Text = text,
            Bytes = bytes,
            EncodingName = encodingName,
            FallbackReason = fallbackReason,
            IsTruncated = truncated
        };
    }

    /// <summary>
    /// 显示用解码：严格 UTF-8 → GB18030 → Latin-1。
    /// 只看前 <see cref="MaxDisplayBytes"/> 字节，截断处可能切在多字节字符中间，用替换字符兜底。
    /// </summary>
    private static (string Text, string EncodingName) DecodeForDisplay(byte[] bytes, out bool truncated)
    {
        truncated = bytes.Length > MaxDisplayBytes;
        var length = Math.Min(bytes.Length, MaxDisplayBytes);
        var slice = new ReadOnlySpan<byte>(bytes, 0, length);

        foreach (var candidate in Candidates())
        {
            var strict = (Encoding)candidate.Clone();
            strict.DecoderFallback = DecoderFallback.ExceptionFallback;
            try
            {
                var decoded = strict.GetString(slice);
                return (truncated ? decoded + TruncationNotice : decoded, Label(candidate));
            }
            catch (DecoderFallbackException)
            {
                // 换下一种编码
            }
        }

        // Latin-1 不会失败：任意字节都能映射，作为最后兜底
        var latin = Encoding.Latin1.GetString(slice);
        return (truncated ? latin + TruncationNotice : latin, "Latin-1");
    }

    private static IEnumerable<Encoding> Candidates()
    {
        yield return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        yield return Encoding.GetEncoding("GB18030");
    }

    private static string Label(Encoding encoding) =>
        encoding.CodePage == Encoding.UTF8.CodePage ? "UTF-8" : encoding.WebName.ToUpperInvariant();

    private const string TruncationNotice =
        "\r\n\r\n--- 源码超过 4 MB，已截断显示；点「另存为 .eml」可获取完整源码 ---";

    private static string AppendReason(string? current, string next) =>
        string.IsNullOrWhiteSpace(current) ? next : current + "；" + next;

    /// <summary>把协议异常翻译成用户能看懂的一句话。</summary>
    private static string DescribeFailure(Exception ex) => ex switch
    {
        AuthenticationException => "登录失败，请检查账号授权码",
        FolderNotFoundException => "服务器上已找不到该邮件或文件夹",
        ServiceNotConnectedException => "无法连接服务器",
        ServiceNotAuthenticatedException => "服务器拒绝了登录",
        ImapProtocolException => "服务器拒绝了读取请求",
        TimeoutException => "连接服务器超时",
        IOException => "网络中断",
        _ => Trim(ex.Message)
    };

    private static string Trim(string message)
    {
        var text = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length == 0) return "未知原因";
        return text.Length <= 100 ? text : text[..100] + "…";
    }
}
