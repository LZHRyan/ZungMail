using ZungMail.Core.Abstractions;
using ZungMail.Core.Services;

namespace ZungMail.Core.Domain;

/// <summary>
/// 摘要相关的默认值与设置归一。放这里是为了让「设置 → 引擎顺序 → 界面文案」
/// 都引用同一份常量，避免各处硬编码引擎 Id 或长度档位。
/// </summary>
public static class MailSummarySettings
{
    /// <summary>本地小模型引擎 Id。</summary>
    public const string LocalModelProviderId = "local-model";

    /// <summary>规则抽取引擎 Id（永远可用的保底引擎）。</summary>
    public const string RuleBasedProviderId = "rule-based";

    /// <summary>设置里的「自动」：本地模型优先，不可用回落规则抽取。</summary>
    public const string AutoProviderId = "auto";

    /// <summary>默认模型文件名（GGUF，Q4_K_M）。</summary>
    public const string DefaultModelFileName = "qwen2.5-0.5b-instruct-q4_k_m.gguf";

    /// <summary>默认模型 Id（目录名与设置项都用它）。</summary>
    public const string DefaultModelId = "qwen2.5-0.5b-instruct-q4_k_m";

    /// <summary>
    /// 默认模型下载地址。发布时由打包流程填写项目 Release 资源地址；
    /// 留空表示「未内置地址」，设置卡片会提示用户自行填写或手动放置文件。
    /// </summary>
    public const string DefaultModelUrl =
        "https://www.modelscope.cn/api/v1/models/Qwen/Qwen2.5-0.5B-Instruct-GGUF/repo" +
        "?Revision=master&FilePath=qwen2.5-0.5b-instruct-q4_k_m.gguf";

    /// <summary>默认模型的 SHA256；留空表示不做校验（仅手动放置场景建议留空）。</summary>
    public const string DefaultModelSha256 =
        "74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db";

    /// <summary>单段字符数：超长正文按段落/句子切成这种大小的块，逐段总结再合并。</summary>
    public const int ChunkChars = 3000;

    /// <summary>最多总结几段（4000 × 6 = 24000 字），再长就只覆盖前 24000 字并在卡片上说明。</summary>
    public const int MaxChunks = 4;

    /// <summary>覆盖上限（总字数）= 单段 × 段数，也是设置里「覆盖上限」的允许区间。</summary>
    public const int MaxInputChars = ChunkChars * MaxChunks;

    public const int MinInputChars = 2000;

    /// <summary>把设置里的引擎值归一成已知 Id；未知值按「自动」处理。</summary>
    public static string NormalizeProvider(string? provider) => (provider ?? "").Trim().ToLowerInvariant() switch
    {
        LocalModelProviderId => LocalModelProviderId,
        RuleBasedProviderId => RuleBasedProviderId,
        _ => AutoProviderId
    };

    /// <summary>
    /// 引擎尝试顺序：期望引擎排第一，规则抽取永远在末尾兜底。
    /// 返回 Id 列表，编排层按此顺序取第一个可用的引擎。
    /// </summary>
    public static IReadOnlyList<string> ProviderOrder(string? provider) => NormalizeProvider(provider) switch
    {
        RuleBasedProviderId => [RuleBasedProviderId],
        LocalModelProviderId => [LocalModelProviderId, RuleBasedProviderId],
        _ => [LocalModelProviderId, RuleBasedProviderId]
    };

    public static MailSummaryLength ResolveLength(string? length) => (length ?? "").Trim().ToLowerInvariant() switch
    {
        "short" => MailSummaryLength.Short,
        "detailed" => MailSummaryLength.Detailed,
        _ => MailSummaryLength.Normal
    };

    public static string LengthToSetting(MailSummaryLength length) => length switch
    {
        MailSummaryLength.Short => "short",
        MailSummaryLength.Detailed => "detailed",
        _ => "normal"
    };

    /// <summary>覆盖上限夹取到允许区间（2000–24000），非法值回落默认 12000。</summary>
    public static int EffectiveInputChars(int chars)
        => Math.Clamp(chars <= 0 ? DefaultCoverageChars : chars, MinInputChars, MaxInputChars);

    /// <summary>默认覆盖上限：12000 字（约 3 段），兼顾覆盖范围与 25 秒推理上限。</summary>
    public const int DefaultCoverageChars = 6000;

    /// <summary>生效的模型下载地址：用户填写优先，其次内置默认。</summary>
    public static string ResolveModelUrl(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultModelUrl : configured.Trim();

    /// <summary>生效的模型校验值：用户填写优先，其次内置默认。</summary>
    public static string ResolveModelSha256(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultModelSha256 : configured.Trim().ToLowerInvariant();
}
