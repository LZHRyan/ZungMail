using System.Globalization;
using System.IO.Compression;
using System.Text;
using ZungMail.Core.Domain;

namespace ZungMail.Application.Services;

/// <summary>资料包导出结果：写入的邮件 / 附件数量与包体积。</summary>
public sealed record ShipmentPackageResult(int Messages, int EmlFiles, int Attachments, long Bytes)
{
    public string Summary => $"已导出 {Messages} 封邮件、{Attachments} 个附件（{DisplayFormat.FileSize(Bytes)}）";
}

/// <summary>
/// 一票货资料包导出：把某票货的全部邮件与附件打成一个 zip。
/// <para>
/// 目录结构：<c>&lt;业务编号&gt;/邮件清单.csv</c>、<c>邮件/NN_主题.eml</c>、<c>附件/NN_文件名</c>。
/// 邮件原文（.eml）优先；没有原文时退化为已缓存的正文（.html / .txt），
/// 附件只在本地已下载时打包（保持「不联网也能导出」的语义，缺失项在清单里留空）。
/// </para>
/// </summary>
public sealed class ShipmentPackageExporter
{
    private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();

    /// <summary>导出到指定 zip 路径；同名文件会被覆盖。</summary>
    public async Task<ShipmentPackageResult> ExportAsync(
        ShipmentCaseExport data,
        string targetPath,
        CancellationToken ct = default)
    {
        var root = SafeName(string.IsNullOrWhiteSpace(data.PrimaryRef) ? "shipment" : data.PrimaryRef);
        var emlCount = 0;
        var attachmentCount = 0;

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        await using var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var manifest = new StringBuilder();
        manifest.AppendLine("序号,时间,发件人,收件人,抄送,主题,附件");
        var index = 0;

        foreach (var message in data.Messages)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            var prefix = index.ToString("D2", CultureInfo.InvariantCulture);
            var subject = string.IsNullOrWhiteSpace(message.Subject) ? "无主题" : message.Subject;
            var baseName = SafeName($"{prefix}_{subject}");

            if (TryReadFile(message.EmlPath) is { } eml)
            {
                await WriteEntryAsync(archive, $"{root}/邮件/{baseName}.eml", eml, ct).ConfigureAwait(false);
                emlCount++;
            }
            else if (!string.IsNullOrWhiteSpace(message.BodyHtml))
            {
                var bytes = Encoding.UTF8.GetBytes(message.BodyHtml!);
                await WriteEntryAsync(archive, $"{root}/邮件/{baseName}.html", bytes, ct).ConfigureAwait(false);
                emlCount++;
            }
            else if (!string.IsNullOrWhiteSpace(message.BodyText))
            {
                var bytes = Encoding.UTF8.GetBytes(message.BodyText!);
                await WriteEntryAsync(archive, $"{root}/邮件/{baseName}.txt", bytes, ct).ConfigureAwait(false);
                emlCount++;
            }

            foreach (var attachment in message.Attachments)
            {
                if (TryReadFile(attachment.LocalPath) is not { } content) continue;

                var fileName = SafeName(string.IsNullOrWhiteSpace(attachment.FileName) ? "attachment" : attachment.FileName);
                await WriteEntryAsync(archive, $"{root}/附件/{prefix}_{fileName}", content, ct).ConfigureAwait(false);
                attachmentCount++;
            }

            manifest.AppendLine(string.Join(',',
                Escape(prefix),
                Escape(DisplayFormat.FullDate(message.DateUtc)),
                Escape(message.FromDisplay),
                Escape(message.ToDisplay),
                Escape(message.CcDisplay),
                Escape(subject),
                Escape(string.Join(" | ", message.Attachments.Select(a => a.FileName)))));
        }

        var manifestBytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(manifest.ToString()))
            .ToArray();
        await WriteEntryAsync(archive, $"{root}/邮件清单.csv", manifestBytes, ct).ConfigureAwait(false);

        var size = stream.Length;
        return new ShipmentPackageResult(data.Messages.Count, emlCount, attachmentCount, size);
    }

    private static byte[]? TryReadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string entryName, byte[] content, CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var target = entry.Open();
        await target.WriteAsync(content, ct).ConfigureAwait(false);
    }

    /// <summary>去掉 Windows 文件名非法字符，压掉空白并限长（zip 内路径仍保持可读）。</summary>
    private static string SafeName(string value)
    {
        var buffer = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            buffer.Append(Array.IndexOf(InvalidFileChars, ch) >= 0 ? '_' : ch);
        }

        var text = buffer.ToString().Trim().TrimEnd('.');
        if (text.Length == 0) text = "item";
        return text.Length <= 60 ? text : text[..60];
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal) + "\"";
    }
}
