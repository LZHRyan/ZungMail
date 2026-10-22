using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace ZungMail.App.Services;

/// <summary>
/// WebView2 环境的进程内缓存：同一个 user data 目录只创建一次
/// <see cref="CoreWebView2Environment"/>。
/// 每个窗口各自 <c>CreateAsync</c> 一次会重复初始化浏览器宿主，
/// 而环境本身与窗口无关，复用它能让多个 WebView2 共用同一个浏览器进程，
/// 也避免"开一次窗口就多一套初始化开销"。
/// </summary>
public static class WebViewEnvironmentCache
{
    private static readonly ConcurrentDictionary<string, Task<CoreWebView2Environment>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static Task<CoreWebView2Environment> GetAsync(string userDataFolder)
    {
        var key = Path.GetFullPath(userDataFolder);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var created = CreateAsync(key);
        Cache[key] = created;
        return created;
    }

    private static async Task<CoreWebView2Environment> CreateAsync(string userDataFolder)
    {
        try
        {
            Directory.CreateDirectory(userDataFolder);
            return await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder).ConfigureAwait(true);
        }
        catch
        {
            // 失败的任务不能被永久缓存，否则之后每次打开都直接失败
            Cache.TryRemove(userDataFolder, out _);
            throw;
        }
    }
}
