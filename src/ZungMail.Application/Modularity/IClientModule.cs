using Microsoft.Extensions.DependencyInjection;

namespace ZungMail.Application.Modularity;

/// <summary>
/// 编译期业务模块契约。当前模块由宿主静态引用并注册；
/// 本接口不承担 DLL 扫描、第三方插件隔离或运行时加载职责。
/// </summary>
public interface IClientModule
{
    /// <summary>稳定、唯一的模块标识，例如 <c>shipment</c>。</summary>
    string Id { get; }

    /// <summary>用于设置页或诊断界面的模块显示名称。</summary>
    string DisplayName { get; }

    /// <summary>在构建服务提供程序前注册模块服务。</summary>
    void RegisterServices(IServiceCollection services);

    /// <summary>在服务提供程序建立后执行异步初始化。</summary>
    Task InitializeAsync(IServiceProvider services, CancellationToken ct = default);
}
