using System.IO;
using ZungMail.App.Services;

namespace ZungMail.App.Services;

/// <summary>
/// 源码另存为 .eml 时的文件名清洗与写盘。
/// 单独抽出来是为了让 UiTests 能在不弹保存对话框的前提下验证命名与字节写入。
/// </summary>
public static class MailSourceFileWriter
{
    /// <summary>空主题或全是非法字符时的兜底文件名。</summary>
    public const string FallbackName = "邮件源码";

    /// <summary>
    /// 默认文件名：主题 + .eml。去掉 Windows 不允许的字符，压到 80 字符以内，
    /// 大小写与中文原样保留（与「作为附件转发」的命名规则一致）。
    /// </summary>
    public static string SuggestFileName(string? subject)
    {
        var text = (subject ?? string.Empty).Trim();
        if (text.Length == 0) text = FallbackName;

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(text.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim(' ', '.');
        if (safe.Length == 0) safe = FallbackName;
        if (safe.Length > 80) safe = safe[..80];

        return safe + ".eml";
    }

    /// <summary>把源码字节原样写入目标文件（不重新编码、不改头部）。</summary>
    public static void Save(byte[] bytes, string path)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("保存路径不能为空。", nameof(path));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllBytes(path, bytes);
    }
}
