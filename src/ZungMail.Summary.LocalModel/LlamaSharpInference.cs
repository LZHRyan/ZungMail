using System.Diagnostics;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using ZungMail.Core.Abstractions;

namespace ZungMail.Summary.LocalModel;

/// <summary>
/// LLamaSharp 实现：懒加载 GGUF 权重、单线程串行推理、空闲 5 分钟自动卸载。
/// <para>
/// 关键点（曾经踩过）：<b>每次推理都要新建并释放 LLamaContext</b>。
/// LLamaContext 自带 KV 缓存，复用同一个 context 再喂一段新 prompt 会让「已缓存位置」与
/// 新输入错位，第二次起直接抛 <c>LLamaDecodeError: llama_decode failed: 'InvalidInputBatch'</c>。
/// 权重（<see cref="LLamaWeights"/>）才是加载成本的大头，可以常驻；context 每次几毫秒。
/// </para>
/// <para>
/// 内存策略：0.5B Q4 权重约占 0.4–0.8 GB，空闲 5 分钟就释放，下次点「AI 总结」再加载。
/// </para>
/// </summary>
public sealed class LlamaSharpInference : ILlamaInference
{
    private static readonly TimeSpan IdleUnloadAfter = TimeSpan.FromMinutes(5);

    /// <summary>上下文窗口：6000 字正文（约 4000–5000 token）+ 最长 720 输出仍有余量。</summary>
    private const int ContextSize = 8192;

    private readonly Func<string?> _modelPathProvider;
    private readonly ILogger<LlamaSharpInference>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private string? _loadedPath;
    private DateTimeOffset _lastUsedUtc;
    private bool _disposed;

    /// <param name="modelPathProvider">返回当前模型文件路径；为空表示还没有模型。</param>
    public LlamaSharpInference(Func<string?> modelPathProvider, ILogger<LlamaSharpInference>? logger = null)
    {
        _modelPathProvider = modelPathProvider;
        _logger = logger;
    }

    public bool IsLoaded
    {
        get
        {
            lock (_stateLock)
            {
                return _weights is not null;
            }
        }
    }

    public async Task<string> CompleteAsync(
        string prompt,
        int maxTokens,
        double temperature,
        IProgress<MailSummaryProgress>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = _modelPathProvider()
                       ?? throw new InvalidOperationException("还没有下载摘要模型，请先到 设置 → 本地 AI 助手 下载。");

            var parameters = EnsureWeightsLoaded(path, progress);

            progress?.Report(new MailSummaryProgress("正在生成摘要", 0.2, "本地模型推理中…"));

            // 每次推理一个全新 context：KV 缓存干净，第二次点击不会因为位置错位而 decode 失败
            using var context = _weights!.CreateContext(parameters);
            var executor = new InteractiveExecutor(context);

            var inference = new InferenceParams
            {
                MaxTokens = Math.Clamp(maxTokens, 64, 1024),
                AntiPrompts = ["\n\n用户：", "\n\nHuman:", "<|im_end|>", "<|im_start|>"],
                // 0.30 起采样参数从 InferenceParams 移到采样管线
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = (float)Math.Clamp(temperature, 0.0, 1.5),
                    TopP = 0.9f,
                    RepeatPenalty = 1.1f
                }
            };

            var builder = new System.Text.StringBuilder(capacity: 1024);
            var started = Stopwatch.StartNew();
            var tokens = 0;

            await foreach (var token in executor.InferAsync(prompt, inference, ct).ConfigureAwait(false))
            {
                builder.Append(token);
                tokens++;

                if (tokens % 8 == 0)
                {
                    progress?.Report(new MailSummaryProgress(
                        "正在生成摘要", null, $"已生成 {tokens} 个片段 · {started.Elapsed.TotalSeconds:F1}s"));
                }
            }

            MarkUsed();
            progress?.Report(new MailSummaryProgress("完成", 1, $"共 {tokens} 个片段，用时 {started.Elapsed.TotalSeconds:F1}s"));
            return builder.ToString();
        }
        catch (OperationCanceledException)
        {
            // 用户取消：上下文随 using 释放，权重留着下次用
            throw;
        }
        catch (Exception)
        {
            // 解码/内存类错误通常与当前上下文状态有关：整套卸载，下一次点击重新加载一个干净实例
            Unload();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Unload()
    {
        LLamaWeights? weights;

        lock (_stateLock)
        {
            weights = _weights;
            _weights = null;
            _modelParams = null;
            _loadedPath = null;
        }

        try
        {
            weights?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "释放摘要模型时出错");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unload();
        _gate.Dispose();
    }

    private void MarkUsed()
    {
        lock (_stateLock)
        {
            _lastUsedUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>按需加载权重；路径变了（换模型）或空闲太久会先卸载。返回可复用的 ModelParams。</summary>
    private ModelParams EnsureWeightsLoaded(string path, IProgress<MailSummaryProgress>? progress)
    {
        lock (_stateLock)
        {
            var idle = DateTimeOffset.UtcNow - _lastUsedUtc;
            if (_weights is not null &&
                _modelParams is not null &&
                string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) &&
                idle <= IdleUnloadAfter)
            {
                return _modelParams;
            }
        }

        // 释放旧模型放在锁外，避免 Dispose 期间被其它线程读到半截状态
        Unload();

        progress?.Report(new MailSummaryProgress("正在加载摘要模型", 0.05, "首次加载约 1 秒…"));

        // 先按 CPU 指令集定位 llama.dll：框架依赖构建下默认搜索找不到 runtimes/win-x64/native/avx2
        LlamaNativeBootstrap.EnsureConfigured();

        var parameters = new ModelParams(path)
        {
            ContextSize = ContextSize,
            GpuLayerCount = 0,
            Threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
            BatchSize = 256
        };

        var weights = LLamaWeights.LoadFromFile(parameters);

        lock (_stateLock)
        {
            _weights = weights;
            _modelParams = parameters;
            _loadedPath = path;
            _lastUsedUtc = DateTimeOffset.UtcNow;
        }

        _logger?.LogInformation("摘要模型已加载：{Path}（上下文 {Context}，线程 {Threads}）",
            path, ContextSize, parameters.Threads);

        return parameters;
    }
}
