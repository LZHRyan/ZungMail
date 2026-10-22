using ZungMail.Update.Protocol;

namespace ZungMail.Core.Abstractions;

/// <summary>反馈提交结果的分类：界面按它决定提示文案与是否提供离线降级。</summary>
public enum FeedbackSubmitStatus
{
    /// <summary>服务端已接收并返回编号。</summary>
    Success,

    /// <summary>没有配置升级服务地址，无法在线提交。</summary>
    NotConfigured,

    /// <summary>地址写了但解析不出 http/https。</summary>
    InvalidAddress,

    /// <summary>DNS / 连接 / 超时等网络层失败。</summary>
    Unreachable,

    /// <summary>服务端拒绝（描述为空、内容过大等 4xx）。</summary>
    Rejected,

    /// <summary>触发服务端限流（429）。</summary>
    RateLimited,

    /// <summary>非 2xx 或响应格式不符合约定。</summary>
    ServerError
}

/// <summary>一次提交的可展示结果。</summary>
public sealed record FeedbackSubmitResult(FeedbackSubmitStatus Status, string Message, string? FeedbackId)
{
    public bool Success => Status == FeedbackSubmitStatus.Success;

    public static FeedbackSubmitResult Ok(string feedbackId, string message)
        => new(FeedbackSubmitStatus.Success, message, feedbackId);

    public static FeedbackSubmitResult Fail(FeedbackSubmitStatus status, string message)
        => new(status, message, null);
}

/// <summary>用户在设置里填的内容；是否附带日志与环境由两个开关决定。</summary>
public sealed record FeedbackDraft(
    string Category,
    string Description,
    string? Contact,
    bool IncludeLog,
    bool IncludeEnvironment);

/// <summary>可附加日志的探测结果：界面用它提示「将附加哪个文件」或「没有可附加的日志」。</summary>
public sealed record FeedbackLogInfo(bool Available, string Description, long Bytes)
{
    public static FeedbackLogInfo None(string description) => new(false, description, 0);
}

/// <summary>
/// 用户反馈：组装内容（环境摘要 + 脱敏日志）并提交到升级服务。
/// 实现不碰 UI，也不在失败时抛异常，便于单测与后台调用。
/// </summary>
public interface IFeedbackService
{
    /// <summary>探测最近可附加的日志文件（只读目录，不读内容）。</summary>
    FeedbackLogInfo InspectLog();

    /// <summary>按草稿组装请求体：环境摘要、脱敏日志与体积截断都在这里完成。</summary>
    Task<FeedbackRequest> BuildRequestAsync(FeedbackDraft draft, CancellationToken ct = default);

    /// <summary>提交反馈；任何失败都返回结果对象。</summary>
    Task<FeedbackSubmitResult> SubmitAsync(FeedbackRequest request, CancellationToken ct = default);
}
