using System.Text;

namespace ZungMail.Core.Abstractions;

/// <summary>摘要长度档位（控制模型最多生成多少 token）。</summary>
public enum MailSummaryLength
{
    /// <summary>短：只要 3 条要点。</summary>
    Short = 0,

    /// <summary>标准：3–5 条要点 + 待处理 + 单号。</summary>
    Normal = 1,

    /// <summary>详细：允许多写几条，并保留更多上下文。</summary>
    Detailed = 2
}

/// <summary>摘要引擎可以处理的一段输入：正文已经过引用折叠、签名剥离与长度截断。</summary>
public sealed record MailSummaryRequest
{
    public required long MessageId { get; init; }

    public string Subject { get; init; } = "";

    public string FromDisplay { get; init; } = "";

    public DateTimeOffset DateUtc { get; init; }

    /// <summary>已清洗的正文（纯文本）。</summary>
    public required string InputText { get; init; }

    /// <summary>原正文是否因为超长被截断（尾部未覆盖）。</summary>
    public bool Truncated { get; init; }

    /// <summary>单段字符数上限：引擎按这个大小把长正文切段后逐段总结。</summary>
    public int ChunkChars { get; init; } = 3000;

    /// <summary>清洗后的正文长度。</summary>
    public int InputChars => InputText.Length;

    public MailSummaryLength Length { get; init; } = MailSummaryLength.Normal;
}

/// <summary>生成中的进度（阶段 + 可选百分比 + 细节文案）。</summary>
public sealed record MailSummaryProgress(string Stage, double? Fraction = null, string? Detail = null);

/// <summary>摘要结果：三段结构化文本 + 来源与耗时，供界面直接展示、复制。</summary>
public sealed class MailSummaryResult
{
    public required long MessageId { get; init; }

    /// <summary>实际使用的引擎 Id（例如 local-model / rule-based）。</summary>
    public required string ProviderId { get; init; }

    /// <summary>引擎展示名，卡片上的徽标文案。</summary>
    public required string ProviderName { get; init; }

    public IReadOnlyList<string> Bullets { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();

    /// <summary>引擎原始输出（复制与排障用）。</summary>
    public string RawText { get; init; } = "";

    /// <summary>正文是否被截断（卡片提示「仅总结前 N 字」）。</summary>
    public bool Truncated { get; init; }

    /// <summary>是否发生了降级（期望的引擎不可用时）。</summary>
    public bool IsFallback { get; init; }

    /// <summary>降级原因；没有降级时为空。</summary>
    public string? FallbackReason { get; init; }

    public int InputChars { get; init; }

    /// <summary>本次总结覆盖了几段（1 表示单段；&gt;1 表示长邮件分段总结后合并）。</summary>
    public int ChunkCount { get; init; } = 1;

    public long ElapsedMs { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>三段合并成可复制的纯文本。</summary>
    public string ToPlainText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("要点：");
        AppendSection(builder, Bullets);
        builder.AppendLine("待处理：");
        AppendSection(builder, Actions);
        builder.AppendLine("单号：");
        AppendSection(builder, References);
        return builder.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder builder, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            builder.AppendLine("- 未提及");
            return;
        }

        foreach (var item in items)
        {
            builder.Append("- ").AppendLine(item);
        }
    }
}

/// <summary>引擎可用性：界面据此显示「未下载模型 / 加载失败」并决定是否降级。</summary>
public sealed record MailSummaryProviderStatus
{
    public required bool Available { get; init; }

    /// <summary>不可用原因（给用户看的一句话）。</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>是否因为缺少模型文件而不可用（界面引导去下载）。</summary>
    public bool NeedsModel { get; init; }
}

/// <summary>没有可总结的正文、或所有引擎都不可用时抛出；界面据此展示原因与重试。</summary>
public sealed class MailSummaryUnavailableException : Exception
{
    public MailSummaryUnavailableException(string message) : base(message)
    {
    }

    public MailSummaryUnavailableException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// 摘要引擎契约。界面与编排只认这个接口：本地小模型、规则抽取、将来的云端接口都实现它，
/// 换引擎不需要改 XAML、数据库或模型文件管理。
/// </summary>
public interface IMailSummaryProvider
{
    /// <summary>稳定 Id，写入设置（local-model / rule-based / 未来的其它引擎）。</summary>
    string Id { get; }

    /// <summary>展示名（卡片徽标与设置下拉）。</summary>
    string DisplayName { get; }

    /// <summary>当前是否可用（模型是否就绪、原生库是否能加载）。</summary>
    Task<MailSummaryProviderStatus> GetStatusAsync(CancellationToken ct = default);

    Task<MailSummaryResult> SummarizeAsync(
        MailSummaryRequest request,
        IProgress<MailSummaryProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>模型文件变化（下载 / 删除 / 重新检测）后清掉引擎内部缓存的失败状态。</summary>
    void ResetState()
    {
        // 默认无状态可清：需要清缓存的引擎自行覆盖
    }
}

/// <summary>
/// 摘要编排：读正文 → 清洗裁剪 → 选引擎（不可用就降级）→ 生成 → 缓存。
/// 只做编排，不包含任何推理实现。
/// </summary>
public interface IMailSummaryService
{
    /// <summary>已选引擎顺序：期望引擎优先，其后是保底引擎。</summary>
    IReadOnlyList<MailSummaryProviderStatus> DescribeProviders();

    /// <summary>取缓存的摘要（同一封邮件、正文与引擎未变）；没有时返回 null。</summary>
    MailSummaryResult? TryGetCached(long messageId);

    /// <summary>生成摘要；<paramref name="force"/> = true 时忽略缓存重新推理。</summary>
    Task<MailSummaryResult> SummarizeAsync(
        long messageId,
        bool force = false,
        IProgress<MailSummaryProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>当前期望引擎（设置项），用于界面提示。</summary>
    string PreferredProviderId { get; }

    /// <summary>模型变化后让各引擎清掉缓存的失败状态（设置页下载 / 删除模型后调用）。</summary>
    void ResetProviders();
}

/// <summary>摘要模型仓库：状态、下载、删除、手动放置后的重新检测。</summary>
public enum SummaryModelState
{
    /// <summary>没有模型文件。</summary>
    Missing = 0,

    /// <summary>正在下载。</summary>
    Downloading = 1,

    /// <summary>已就绪可用。</summary>
    Ready = 2,

    /// <summary>文件在但不可用（损坏 / 校验失败 / 无法加载）。</summary>
    Invalid = 3
}

/// <summary>模型仓库状态快照。</summary>
public sealed record SummaryModelStatus(
    SummaryModelState State,
    string ModelId,
    string FilePath,
    long SizeBytes,
    string? Detail);

/// <summary>摘要模型（GGUF）的获取与校验；实现负责把文件放到本机数据目录。</summary>
public interface ISummaryModelStore
{
    /// <summary>模型目录（用户可手动放置文件）。</summary>
    string ModelDirectory { get; }

    SummaryModelStatus GetStatus();

    /// <summary>下载模型；已存在且校验通过时直接返回就绪。</summary>
    Task<SummaryModelStatus> DownloadAsync(
        IProgress<MailSummaryProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>删除模型文件。</summary>
    void Delete();

    /// <summary>重新检测（用户手动拷贝文件后调用）。</summary>
    SummaryModelStatus Recheck();
}
