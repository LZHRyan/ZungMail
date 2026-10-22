using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;

namespace ZungMail.App.Services;

/// <summary>
/// 「回复 / 回复全部 / 转发」的撰写请求装配。
/// 主窗口与独立阅读窗口共用这一份实现：引用块（<see cref="MailQuoteBuilder"/>）、
/// 收件人推导、回复线索只有一处，避免两个窗口的行为慢慢漂移。
/// </summary>
public static class ComposeRequestFactory
{
    /// <summary>
    /// 生成回复 / 回复全部 / 转发的撰写请求；草稿邮件不允许回复转发时返回 <c>null</c>。
    /// </summary>
    /// <param name="selfEmail">当前账户地址，用于「回复全部」时把自己从抄送里排除。</param>
    /// <param name="readLocalImage">读取正文内嵌图片（<c>file:///</c>）的委托；为空则引用里不带图。</param>
    public static ComposeRequest? ForReply(
        EmailDetail detail,
        ComposeMode mode,
        string selfEmail,
        Func<string, byte[]?>? readLocalImage = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (mode == ComposeMode.New) throw new ArgumentOutOfRangeException(nameof(mode), "新写邮件不需要引用原文");

        if (!MailActionGuards.CanReplyOrForward(detail)) return null;

        var sender = new EmailAddress(detail.ReplyTo?.Address ?? detail.FromEmail, detail.FromName);

        var to = mode == ComposeMode.Forward ? string.Empty : sender.Display;
        var cc = mode == ComposeMode.ReplyAll
            ? FormatAddresses(detail.Cc.Concat(detail.To), selfEmail, sender.Address)
            : string.Empty;

        var subject = mode == ComposeMode.Forward
            ? SubjectPrefix(detail.Subject, "Fwd:")
            : SubjectPrefix(detail.Subject, "Re:");

        return new ComposeRequest
        {
            Mode = mode,
            AccountId = detail.AccountId,
            To = to,
            Cc = cc,
            Subject = subject,
            Body = BuildQuoteHtml(detail, readLocalImage),
            // 转发不带回复线索，避免收件端错误聚合会话（FR-05）
            InReplyTo = mode == ComposeMode.Forward ? null : detail.MessageId,
            References = mode == ComposeMode.Forward ? null : JoinReferences(detail)
        };
    }

    /// <summary>引用块 HTML（抬头 + 原邮件正文，内嵌图片就地内联）。</summary>
    public static string BuildQuoteHtml(EmailDetail detail, Func<string, byte[]?>? readLocalImage = null)
        => MailQuoteBuilder.Build(detail, readLocalImage);

    /// <summary>
    /// 读取正文里 <c>file:///</c> 图片的字节。只放行附件目录内的文件，
    /// 避免正文里被塞入指向本机其它位置的引用。
    /// </summary>
    public static byte[]? ReadLocalImage(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MailQuoteBuilder.MaxInlineImageBytes) return null;

            var full = Path.GetFullPath(path);
            var prefix = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            return File.ReadAllBytes(full);
        }
        catch (Exception)
        {
            // 路径非法 / 文件被删 / 无权限：当作这张图取不到
            return null;
        }
    }

    /// <summary>地址列表转「显示名 &lt;地址&gt;」，排除自己与指定地址，去重保序。</summary>
    internal static string FormatAddresses(IEnumerable<EmailAddress> addresses, string self, params string[] exclude)
    {
        var items = addresses
            .Where(a => !string.IsNullOrWhiteSpace(a.Address))
            .Where(a => !string.Equals(a.Address, self, StringComparison.OrdinalIgnoreCase))
            .Where(a => !exclude.Any(e => string.Equals(a.Address, e, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Display)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(", ", items);
    }

    /// <summary>补前缀（Re: / Fwd:），已有同一前缀时不重复加。</summary>
    internal static string SubjectPrefix(string subject, string prefix)
    {
        var trimmed = (subject ?? string.Empty).TrimStart();
        if (trimmed.Length == 0) return prefix + " ";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? trimmed : $"{prefix} {trimmed}";
    }

    /// <summary>Reference 链 = 原 References + 本封 Message-ID。</summary>
    internal static string? JoinReferences(EmailDetail detail)
    {
        var joined = string.Join(" ", detail.References
            .Concat(new[] { detail.MessageId })
            .Where(r => !string.IsNullOrWhiteSpace(r)));

        return joined.Length == 0 ? null : joined;
    }
}
