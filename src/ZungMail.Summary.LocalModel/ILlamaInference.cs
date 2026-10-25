using ZungMail.Core.Abstractions;

namespace ZungMail.Summary.LocalModel;

/// <summary>
/// 推理过程的抽象：把 LLamaSharp 的加载/采样细节关在这一层后面。
/// 好处是单元测试可以用假实现验证提示词与解析，CI 不需要真模型与原生库。
/// </summary>
public interface ILlamaInference : IDisposable
{
    /// <summary>模型是否已加载（未加载时第一次推理会加载）。</summary>
    bool IsLoaded { get; }

    /// <summary>
    /// 推理一次，返回模型生成的文本。实现必须保证每次调用使用干净的 KV 缓存
    /// （LLamaSharp 里就是「每次新建 LLamaContext」），否则第二次调用会抛 LLamaDecodeError。
    /// </summary>
    Task<string> CompleteAsync(
        string prompt,
        int maxTokens,
        double temperature,
        IProgress<MailSummaryProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>卸载模型，释放内存（空闲或切换模型时调用）。</summary>
    void Unload();
}
