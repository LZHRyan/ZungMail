using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.Services;

/// <summary>
/// 摘要引擎插件的发现与注册。App 不编译期依赖任何具体引擎实现：
/// 按名字找到模块类型 → 调它的静态 <c>Register(IServiceCollection)</c>。
/// <para>
/// 任何一个模块加载失败（程序集没随包发布、LLamaSharp 原生库缺依赖、构造抛异常）
/// 都只让该引擎变成「不可用」，规则抽取仍在，客户端照常启动。
/// </para>
/// </summary>
internal static class SummaryModuleCatalog
{
    /// <summary>已知的引擎模块（Type.GetType 的「类型全名, 程序集名」格式）。新增引擎在这里加一行。</summary>
    private static readonly string[] ModuleTypeNames =
    [
        "ZungMail.Summary.LocalModel.LocalModelModule, ZungMail.Summary.LocalModel"
    ];

    /// <summary>注册所有能加载的引擎模块，返回每个模块的加载结果（供启动日志诊断）。</summary>
    public static IReadOnlyList<string> Register(IServiceCollection services, ILogger? logger = null)
    {
        var results = new List<string>();

        foreach (var typeName in ModuleTypeNames)
        {
            try
            {
                var type = Type.GetType(typeName, throwOnError: false);
                if (type is null)
                {
                    results.Add($"{typeName} 未找到");
                    continue;
                }

                var register = type.GetMethod("Register", BindingFlags.Public | BindingFlags.Static);
                if (register is null)
                {
                    results.Add($"{typeName} 缺少静态 Register 方法");
                    continue;
                }

                register.Invoke(null, [services]);
                results.Add($"{type} 已注册");
            }
            catch (Exception ex)
            {
                // 反射调用会把真实异常包在 TargetInvocationException 里
                var root = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                results.Add($"{typeName} 注册失败：{root.GetType().Name} {root.Message}");
                logger?.LogWarning(root, "摘要引擎插件注册失败：{Module}", typeName);
            }
        }

        return results;
    }
}
