using System.Reflection;

namespace ZungMail.App.Services;

/// <summary>
/// 版本号的单一来源：取程序集信息版本（由 Directory.Build.props 写入），
/// 去掉源码链接后缀（+sha），避免标题栏与关于页各算一套。
/// </summary>
public static class AppVersionInfo
{
    /// <summary>不带前缀的版本号，例如 1.2.0。</summary>
    public static string Display => Trim(Informational());

    /// <summary>带 v 前缀的版本号，例如 v1.2.0。</summary>
    public static string DisplayWithPrefix => $"v{Display}";

    private static string? Informational() =>
        typeof(AppVersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static string Trim(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            var version = typeof(AppVersionInfo).Assembly.GetName().Version;
            return version is null ? "未知" : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }
}
