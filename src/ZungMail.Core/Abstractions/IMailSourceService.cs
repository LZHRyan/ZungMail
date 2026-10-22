namespace ZungMail.Core.Abstractions;

/// <summary>源码来源：服务器上的原始 MIME，或按本地库字段重建的邮件。</summary>
public enum MailSourceKind
{
    /// <summary>服务器返回的原始 RFC 822 源码（UID FETCH BODY[]）。</summary>
    ServerRaw = 0,

    /// <summary>服务器取不到时，按本地库字段重建的邮件（与「作为附件转发」同一套逻辑）。</summary>
    Rebuilt = 1
}

/// <summary>
/// 一次「查看邮件源码」的结果：窗口显示用的文本、另存用的原始字节，以及来源与解码说明。
/// <para>
/// <see cref="Bytes"/> 始终是完整字节（另存 .eml 用）；<see cref="Text"/> 可能因为超长被截断。
/// </para>
/// </summary>
public sealed record MailSourceSnapshot
{
    public required long MessageId { get; init; }

    public required MailSourceKind Kind { get; init; }

    /// <summary>窗口里显示的源码文本（可能截断）。</summary>
    public required string Text { get; init; }

    /// <summary>另存 .eml 写出的完整字节；原始源码时与服务器返回一致。</summary>
    public required byte[] Bytes { get; init; }

    /// <summary>实际用来解码显示的编码名，例如 UTF-8 / GB18030 / Latin-1。</summary>
    public required string EncodingName { get; init; }

    /// <summary>回退到本地重建的原因；服务器原始源码时为空。</summary>
    public string? FallbackReason { get; init; }

    /// <summary>显示内容被截断（另存仍完整）。</summary>
    public bool IsTruncated { get; init; }

    public DateTimeOffset GeneratedUtc { get; init; } = DateTimeOffset.UtcNow;

    public long SizeBytes => Bytes.LongLength;

    /// <summary>窗口顶部的来源徽标文案。</summary>
    public string KindLabel => Kind == MailSourceKind.ServerRaw ? "服务器原始源码" : "本地重建源码";
}

/// <summary>既取不到服务器原文、也没有本地正文可重建时抛出，界面据此展示失败原因与重试。</summary>
public sealed class MailSourceUnavailableException : Exception
{
    public MailSourceUnavailableException(string message) : base(message)
    {
    }

    public MailSourceUnavailableException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// 查看邮件源码：优先取服务器原始 MIME，失败回退本地重建。
/// 实现不得改动邮件的已读/星标状态，也不写库。
/// </summary>
public interface IMailSourceService
{
    /// <param name="messageId">本地邮件 Id。</param>
    /// <param name="preferServerSource">true = 先试服务器原文（默认）；false = 直接用本地重建。</param>
    Task<MailSourceSnapshot> LoadAsync(
        long messageId,
        bool preferServerSource = true,
        CancellationToken ct = default);
}
