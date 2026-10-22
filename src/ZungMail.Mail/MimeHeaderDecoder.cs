using System.Text;
using MimeKit.Utils;

namespace ZungMail.Mail;

/// <summary>补齐部分 IMAP 服务器在 ENVELOPE 中原样返回 RFC 2047 编码主题的兼容解码。</summary>
internal static class MimeHeaderDecoder
{
    public static string DecodeSubject(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("=?", StringComparison.Ordinal))
        {
            return value ?? string.Empty;
        }

        try
        {
            // RFC 2047 encoded-word 本身仅使用 ASCII；MimeKit 根据其中 charset 自动选择 GBK、KOI8-R、Windows-1251 等编码。
            var decoded = Rfc2047.DecodeText(Encoding.ASCII.GetBytes(value));
            return string.IsNullOrWhiteSpace(decoded) ? value : decoded;
        }
        catch (Exception)
        {
            // 非法 encoded-word 不阻断同步，保留服务器原值以便后续排查。
            return value;
        }
    }
}
