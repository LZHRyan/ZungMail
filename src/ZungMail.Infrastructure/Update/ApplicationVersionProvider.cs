using System.Reflection;
using ZungMail.Core.Abstractions;
using ZungMail.Update.Protocol;

namespace ZungMail.Infrastructure.Update;

/// <summary>
/// 读取客户端自身版本：优先程序集信息版本（Directory.Build.props 的 &lt;Version&gt;），
/// 去掉源码链接后缀（<c>+commit</c>），并规范成三段数字。
/// </summary>
public sealed class ApplicationVersionProvider : IApplicationVersion
{
    public ApplicationVersionProvider()
        : this(Assembly.GetEntryAssembly())
    {
    }

    public ApplicationVersionProvider(Assembly? assembly)
    {
        Version = Resolve(assembly ?? typeof(ApplicationVersionProvider).Assembly);
    }

    public string Version { get; }

    private static string Resolve(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var value = plus > 0 ? informational[..plus] : informational;
            if (UpdateVersion.TryParse(value, out var parsed)) return parsed.ToString();
        }

        var version = assembly.GetName().Version;
        if (version is not null)
        {
            var text = $"{Math.Max(version.Major, 0)}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}";
            if (UpdateVersion.TryParse(text, out var parsed)) return parsed.ToString();
        }

        return "1.0.0";
    }
}
