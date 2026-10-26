using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using ZungMail.App.Views;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ZungMail.App.Services;

/// <summary>
/// 附件操作：内置预览 / 本机打开 / 复制到剪贴板 / 另存 / 定位 / 全部保存。
/// 主窗口阅读区与独立阅读窗口共用同一套行为与提示文案，避免两边各写一份后逐渐漂移。
/// 每个方法返回需要显示给用户的提示；返回 null 表示无需提示（正常完成或用户取消）。
/// </summary>
public sealed class AttachmentActionService
{
    private readonly ISettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly IMailSyncService _sync;
    private readonly ILogger<AttachmentActionService> _logger;

    public AttachmentActionService(
        ISettingsStore settings,
        IDialogService dialogs,
        IMailSyncService sync,
        ILogger<AttachmentActionService> logger)
    {
        _settings = settings;
        _dialogs = dialogs;
        _sync = sync;
        _logger = logger;
    }

    /// <summary>附件没下载时的统一提示（预览 / 另存 / 定位都走这句）。</summary>
    public const string NotDownloadedHint = "附件还没下载（当前账户收取策略不含附件），右键选择「下载附件」后再打开";

    /// <summary>
    /// 「仅收取正文」账户的附件是后下载的：这里按需把该邮件的附件拉下来。
    /// 返回给用户的提示；<c>null</c> 表示无需提示。
    /// </summary>
    public async Task<string?> DownloadAsync(AttachmentInfo? attachment)
    {
        if (attachment is null || attachment.EmailId <= 0) return null;
        if (attachment.IsDownloaded) return "附件已经下载到本地";

        try
        {
            var ok = await _sync.DownloadBodyAsync(attachment.EmailId, includeAttachments: true).ConfigureAwait(true);
            return ok ? "附件已下载，可以再次点击打开" : "附件下载失败，请检查网络后重试";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "按需下载附件失败：邮件 {EmailId}", attachment.EmailId);
            return "附件下载失败：" + ex.Message;
        }
    }

    /// <summary>附件双击：优先内置预览（受设置控制），不支持或已关闭时回退到系统程序。</summary>
    public string? Preview(AttachmentInfo? attachment)
    {
        if (attachment is not { IsDownloaded: true } file) return NotDownloadedHint;

        var path = file.LocalPath!;
        if (!File.Exists(path)) return null;

        if (_settings.Current.UseInternalAttachmentPreview
            && AttachmentPreviewWindow.CanPreview(file.FileName)
            && _dialogs.ShowAttachmentPreview(file.FileName, path))
        {
            return null;
        }

        return Open(path);
    }

    public string? Open(string? path)
    {
        if (path is not { Length: > 0 } file || !File.Exists(file)) return null;

        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开附件失败：{Path}", file);
            return "无法打开附件：" + ex.Message;
        }
    }

    /// <summary>
    /// 右键「复制」/ Ctrl+C：把附件文件放进剪贴板（FileDrop + 路径文本），
    /// 可直接粘到资源管理器、聊天窗口或新邮件里。
    /// </summary>
    public string? Copy(AttachmentInfo? attachment)
    {
        if (attachment is not { IsDownloaded: true } file) return NotDownloadedHint;

        if (file.LocalPath is not { Length: > 0 } path || !File.Exists(path))
        {
            return "附件文件不存在，请重新下载后再复制";
        }

        var data = AttachmentClipboard.BuildDataObject(path);

        // 剪贴板是全局资源：别的程序短暂占用时会抛 ExternalException，退一下重试两次
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return "已复制附件：" + file.FileName;
            }
            catch (ExternalException ex)
            {
                _logger.LogDebug(ex, "剪贴板被占用，复制附件第 {Attempt} 次重试", attempt);
                Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "复制附件到剪贴板失败：{Path}", path);
                return "复制附件失败：" + ex.Message;
            }
        }

        return "复制失败：剪贴板被其它程序占用，请稍后再试";
    }

    /// <summary>附件另存为：把已下载到本地的副本复制到用户指定位置。</summary>
    public string? Save(AttachmentInfo? attachment)
    {
        if (attachment is not { IsDownloaded: true } file) return NotDownloadedHint;

        var dialog = new SaveFileDialog
        {
            Title = "另存附件",
            FileName = file.FileName,
            Filter = "所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return null;

        try
        {
            File.Copy(file.LocalPath!, dialog.FileName, overwrite: true);
            return "附件已保存：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "另存附件失败：{Path}", file.LocalPath);
            return "保存附件失败：" + ex.Message;
        }
    }

    /// <summary>在资源管理器中定位附件文件。</summary>
    public string? Reveal(AttachmentInfo? attachment)
    {
        if (attachment is not { IsDownloaded: true } file) return NotDownloadedHint;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.LocalPath}\"")
            {
                UseShellExecute = true
            });
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "定位附件失败：{Path}", file.LocalPath);
            return "无法打开附件所在目录：" + ex.Message;
        }
    }

    /// <summary>把给定邮件的全部非内嵌附件保存到同一目录（重名自动加序号）。</summary>
    public string? SaveAll(IReadOnlyList<AttachmentInfo> attachments)
    {
        var files = attachments.Where(a => a.IsDownloaded && !a.IsInline).ToList();
        if (files.Count == 0) return "没有可保存的附件";

        var dialog = new OpenFolderDialog { Title = "选择附件保存位置" };
        if (dialog.ShowDialog() != true) return null;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var saved = 0;
        foreach (var file in files)
        {
            try
            {
                var name = UniqueFileName(file.FileName, used);
                File.Copy(file.LocalPath!, Path.Combine(dialog.FolderName, name), overwrite: false);
                saved++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "批量保存附件失败：{Path}", file.LocalPath);
            }
        }

        return saved == files.Count
            ? $"已保存 {saved} 个附件到 {dialog.FolderName}"
            : $"已保存 {saved} / {files.Count} 个附件，其余失败";
    }

    private static string UniqueFileName(string fileName, HashSet<string> used)
    {
        if (used.Add(fileName)) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }
}
