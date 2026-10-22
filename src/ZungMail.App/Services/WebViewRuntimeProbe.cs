using System;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace ZungMail.App.Services;

/// <summary>
/// WebView2 运行时探测：本机未安装 Evergreen Runtime 时，阅读区自动回退为纯文本。
/// 结果缓存一次，避免每封邮件都走一次注册表 / 文件探测。
/// </summary>
public static class WebViewRuntimeProbe
{
    private static bool? _available;
    private static string? _version;

    /// <summary>本机是否具备可用的 WebView2 运行时。</summary>
    public static bool IsAvailable => _available ??= Detect();

    /// <summary>探测到的运行时版本号，不可用时为 null。</summary>
    public static string? Version => _version;

    /// <summary>WebView2 用户数据目录。放在 LocalAppData，避免 Roaming 同步数十 MB 缓存。</summary>
    public static string GetUserDataFolder()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZungMail",
            "WebView2");

    private static bool Detect()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version)) return false;

            _version = version;
            return true;
        }
        catch (Exception)
        {
            // 未安装运行时（WebView2RuntimeNotFoundException）或运行时目录损坏
            return false;
        }
    }
}
