using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZungMail.Core.Abstractions;

namespace ZungMail.Summary.LocalModel;

/// <summary>
/// 本地模型引擎的插件入口：App 通过反射调用 <see cref="Register"/>，
/// 所以这个程序集缺文件、原生库坏掉都只影响「本地模型」这一个引擎，客户端照常启动。
/// </summary>
public static class LocalModelModule
{
    /// <summary>模块全名（App 的插件目录里登记的就是这个名字）。</summary>
    public const string ModuleTypeName = "ZungMail.Summary.LocalModel.LocalModelModule, ZungMail.Summary.LocalModel";

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<ILlamaInference>(provider =>
        {
            var store = provider.GetRequiredService<ISummaryModelStore>();
            var logger = provider.GetService<ILogger<LlamaSharpInference>>();

            return new LlamaSharpInference(
                () => store.GetStatus() is { State: SummaryModelState.Ready } status ? status.FilePath : null,
                logger);
        });

        services.AddSingleton<IMailSummaryProvider>(provider => new LocalModelSummaryProvider(
            provider.GetRequiredService<ISummaryModelStore>(),
            provider.GetRequiredService<ILlamaInference>(),
            provider.GetService<ILogger<LocalModelSummaryProvider>>()));
    }
}
