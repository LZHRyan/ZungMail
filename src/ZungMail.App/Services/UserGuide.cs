using System;
using System.Diagnostics;
using System.IO;

namespace ZungMail.App.Services;

/// <summary>
/// 随程序发布的离线 HTML 使用教程：解析安装目录路径、检查文件并用默认浏览器打开。
/// 教程不依赖网络，缺失时只返回可读错误，不弹系统错误框。
/// </summary>
public static class UserGuide
{
    /// <summary>教程相对程序目录的位置；publish / NSIS 安装包都会保留这个目录结构。</summary>
    public const string RelativePath = @"Assets\help\user-guide.html";

    public static string ResolvePath(string baseDirectory)
        => Path.GetFullPath(Path.Combine(baseDirectory, RelativePath));

    /// <summary>
    /// 打开教程。launch 仅供测试注入；生产调用传 null，使用系统默认浏览器。
    /// </summary>
    public static bool TryOpen(string baseDirectory, out string error, Action<string>? launch = null)
    {
        var path = ResolvePath(baseDirectory);
        if (!File.Exists(path))
        {
            error = "找不到使用教程文件：" + path;
            return false;
        }

        try
        {
            (launch ?? LaunchWithShell)(path);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = "无法打开使用教程：" + ex.Message;
            return false;
        }
    }

    private static void LaunchWithShell(string path)
    {
        var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        if (process is null)
        {
            throw new InvalidOperationException("系统没有可打开 HTML 文件的默认程序。");
        }
    }
}
