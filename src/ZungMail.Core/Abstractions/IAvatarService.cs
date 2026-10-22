namespace ZungMail.Core.Abstractions;

/// <summary>一次头像查询的结果类别（用于统计与日志，不区分具体失败原因）。</summary>
public enum AvatarLookupStatus
{
    /// <summary>刚从服务器拿到头像。</summary>
    Hit,

    /// <summary>本地缓存已有，未发请求。</summary>
    Cached,

    /// <summary>服务器明确表示没有这个头像（Gravatar 404），一段时间内不再重复查。</summary>
    NotFound,

    /// <summary>网络不可用、超时或服务器异常：什么都不记，下次运行再试。</summary>
    Unavailable
}

/// <summary>头像查询结果：<see cref="Image"/> 为原始图片字节（PNG/JPEG 由 WPF 自行识别）。</summary>
public readonly record struct AvatarLookupResult(byte[]? Image, AvatarLookupStatus Status)
{
    public bool HasImage => Image is { Length: > 0 };

    public static readonly AvatarLookupResult NotFound = new(null, AvatarLookupStatus.NotFound);

    public static readonly AvatarLookupResult Unavailable = new(null, AvatarLookupStatus.Unavailable);
}

/// <summary>
/// 联系人头像来源。IMAP / SMTP 没有取头像或通讯录的标准接口，
/// 所以当前实现只有 Gravatar 这一条公开查询（按邮箱地址哈希取图）。
/// </summary>
public interface IAvatarService
{
    /// <summary>取该邮箱地址的头像；不支持、没有或网络不可用时返回 null 图像，绝不抛异常。</summary>
    Task<AvatarLookupResult> GetAsync(string? email, CancellationToken ct = default);
}
