using ZungMail.Update.Protocol;

namespace ZungMail.Core.Abstractions;

/// <summary>一次升级检测的结果分类（界面与自动检测据此决定展示方式）。</summary>
public enum UpdateCheckStatus
{
    /// <summary>用户设置与安装目录都没有升级服务地址，不发起请求。</summary>
    NotConfigured,

    /// <summary>地址已填写但无法解析成 http/https 地址。</summary>
    InvalidAddress,

    /// <summary>服务端返回了不高于本地的版本。</summary>
    UpToDate,

    /// <summary>发现更高版本。</summary>
    UpdateAvailable,

    /// <summary>DNS / 连接 / 超时等网络层失败。</summary>
    Unreachable,

    /// <summary>非 2xx、响应格式错误或协议不兼容。</summary>
    ServerError
}

/// <summary>升级地址来源，设置页据此告诉用户地址从哪来。</summary>
public enum UpdateAddressSource
{
    UserSetting,
    InstallFile,
    Missing
}

/// <summary>地址解析结果：失败时 <see cref="Message"/> 是可直接展示的中文原因。</summary>
public sealed record UpdateAddressResolution(
    bool Success,
    UpdateAddressSource Source,
    Uri? Address,
    string Message)
{
    public static UpdateAddressResolution Missing(string message)
        => new(false, UpdateAddressSource.Missing, null, message);

    public static UpdateAddressResolution Invalid(UpdateAddressSource source, string message)
        => new(false, source, null, message);

    public static UpdateAddressResolution Ok(UpdateAddressSource source, Uri address)
        => new(true, source, address, "");
}

/// <summary>一次检测的可展示结果。</summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string Message,
    UpdateReleaseInfo? Release,
    Uri? ServiceAddress,
    DateTimeOffset CheckedAt)
{
    public bool HasUpdate => Status == UpdateCheckStatus.UpdateAvailable && Release is not null;

    public static UpdateCheckResult Fail(UpdateCheckStatus status, string message, Uri? address = null)
        => new(status, message, null, address, DateTimeOffset.Now);
}

/// <summary>下载进度；<see cref="TotalBytes"/> 为空表示服务端没给长度。</summary>
public sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double Percent => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100.0 / TotalBytes.Value, 0, 100)
        : 0;

    public bool IsIndeterminate => TotalBytes is not > 0;
}

/// <summary>下载结果：失败时 <see cref="Message"/> 用于界面提示。</summary>
public sealed record UpdateDownloadResult(bool Success, string? FilePath, string Message, bool Reused = false)
{
    public static UpdateDownloadResult Ok(string path, bool reused)
        => new(true, path, reused ? "已复用本机安装包" : "下载完成", reused);

    public static UpdateDownloadResult Fail(string message) => new(false, null, message);
}

/// <summary>应用自身版本（用于与升级服务返回的版本比较）。</summary>
public interface IApplicationVersion
{
    string Version { get; }
}

/// <summary>
/// 客户端升级服务：解析地址、检测新版本、下载并校验安装包。
/// 具体实现不碰 UI，也不负责安装，便于在后台宿主服务与单元测试里复用。
/// </summary>
public interface IUpdateService
{
    /// <summary>本机下载安装包的根目录（`%LOCALAPPDATA%\ZungMail\updates`）。</summary>
    string DownloadsDirectory { get; }

    /// <summary>按当前设置与安装目录 update.json 解析服务地址。</summary>
    UpdateAddressResolution ResolveAddress();

    /// <summary>检测新版本；任何失败都返回结果对象，不抛异常。</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default);

    /// <summary>下载并校验安装包；已存在同版本且哈希一致时直接复用。</summary>
    Task<UpdateDownloadResult> DownloadAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default);
}
