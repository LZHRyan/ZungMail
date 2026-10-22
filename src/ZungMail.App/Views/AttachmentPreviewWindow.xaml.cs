using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ZungMail.App.Services;
using Microsoft.Web.WebView2.Core;

namespace ZungMail.App.Views;

/// <summary>
/// 附件内置预览：PDF / 图片交给 WebView2 原生渲染，纯文本转义后按等宽排版。
/// 不支持的格式（Office、压缩包等）由调用方回退到系统程序，避免出现空白窗口。
/// </summary>
public partial class AttachmentPreviewWindow : DialogWindow
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".json", ".xml", ".html", ".htm", ".md", ".ini", ".yml", ".yaml", ".sql"
    };

    private readonly string _path;
    private readonly string _userDataFolder;
    private bool _browserReleased;

    public AttachmentPreviewWindow(string fileName, string path, string dataDirectory)
    {
        InitializeComponent();
        _path = path;
        // 预览缓存跟着数据目录走：换目录后不再在默认目录里重建一份
        _userDataFolder = Path.Combine(dataDirectory, "WebView2Preview");
        TitleText.Text = fileName;
        Title = fileName;
        Loaded += OnLoaded;
        Closed += (_, _) => ReleaseBrowser();
    }

    /// <summary>窗口关闭后释放 WebView2，否则每预览一次附件就多留一份渲染进程。</summary>
    private void ReleaseBrowser()
    {
        if (_browserReleased) return;
        _browserReleased = true;

        try { Browser.Dispose(); }
        catch (Exception)
        {
            // 释放失败交给进程退出兜底
        }
    }

    /// <summary>判断某扩展名是否适合内置预览，供调用方决定是否回退到系统程序。</summary>
    public static bool CanPreview(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (TextExtensions.Contains(extension)) return true;

        return extension.ToLowerInvariant() switch
        {
            ".pdf" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" => true,
            _ => false
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = _userDataFolder;
            Directory.CreateDirectory(userDataFolder);

            // 同一目录复用同一个浏览器宿主：预览多次也只初始化一次
            var environment = await WebViewEnvironmentCache.GetAsync(userDataFolder);
            if (_browserReleased) return;

            await Browser.EnsureCoreWebView2Async(environment);
            if (_browserReleased) return;

            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            if (TextExtensions.Contains(Path.GetExtension(_path)))
            {
                // 只读前 200 万字符：几十 MB 的 .log/.csv 直接 ReadAllText 会瞬间吃掉几百 MB
                var (text, truncated) = await ReadTextHeadAsync(_path, MaxPreviewChars);
                if (_browserReleased) return;

                if (truncated) text += "\n\n…（文件较大，只预览前 200 万个字符）";
                core.NavigateToString(BuildTextDocument(text));
            }
            else
            {
                core.Navigate(new Uri(_path).AbsoluteUri);
            }
        }
        catch (Exception ex)
        {
            // 窗口已关闭时控件已释放，不要再碰它
            if (_browserReleased) return;

            Browser.Visibility = Visibility.Collapsed;
            HintText.Visibility = Visibility.Visible;
            HintText.Text = "无法预览该附件：" + ex.Message;
        }
    }

    /// <summary>预览字符上限：约 4 MB（UTF-16），足够看清日志上下文又不至于把内存打满。</summary>
    private const int MaxPreviewChars = 2_000_000;

    /// <summary>流式读取文件开头若干字符：不把整个文件读成字符串。</summary>
    private static async Task<(string Text, bool Truncated)> ReadTextHeadAsync(string path, int maxChars)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, useAsync: true);
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: true);

        var buffer = new char[maxChars + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (read <= 0) break;
            total += read;
        }

        var truncated = total > maxChars;
        return (new string(buffer, 0, Math.Min(total, maxChars)), truncated);
    }

    private static string BuildTextDocument(string text)
        => "<!doctype html><html><head><meta charset=\"utf-8\"><style>" +
           "body{margin:0;padding:18px;font:13px/1.6 'Cascadia Mono',Consolas,'Microsoft YaHei',monospace;" +
           "white-space:pre-wrap;word-break:break-all;color:#1f2937;background:#fff}" +
           "</style></head><body>" + System.Net.WebUtility.HtmlEncode(text) + "</body></html>";

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnOpenExternalClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageDialog.Alert("打开失败", ex.Message, DialogIcon.Warning, owner: this);
        }
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存附件",
            FileName = Path.GetFileName(_path),
            Filter = "所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.Copy(_path, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            MessageDialog.Alert("保存失败", ex.Message, DialogIcon.Warning, owner: this);
        }
    }
}
