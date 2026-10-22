namespace ZungMail.Update.Protocol;

/// <summary>
/// 客户端与升级服务之间的固定接口约定。客户端只配置服务根地址，
/// 具体路径一律由本类拼接，升级服务也用它注册路由，避免两边写错。
/// </summary>
public static class UpdateRoutes
{
    /// <summary>协议版本：不兼容变更时递增，客户端据此提示升级服务版本过旧。</summary>
    public const int ProtocolVersion = 1;

    public const string ServiceName = "ZungMail.UpdateServer";

    public const string Ping = "/api/update/ping";
    public const string Check = "/api/update/check";
    public const string DownloadPrefix = "/api/update/download";
    public const string Health = "/healthz";

    /// <summary>用户反馈提交（公开，不需要管理员会话）。</summary>
    public const string Feedback = "/api/feedback";

    /// <summary>管理页的反馈列表 / 详情 / 删除前缀。</summary>
    public const string AdminFeedback = "/api/admin/feedback";

    /// <summary>反馈请求体上限：客户端组装时截断日志，服务端按同一数值拒绝超限请求。</summary>
    public const int MaxFeedbackBytes = 1024 * 1024;

    /// <summary>客户端附加日志的默认上限（末尾片段）。</summary>
    public const int MaxFeedbackLogBytes = 512 * 1024;

    public const string CurrentParameter = "current";
    public const string ChannelParameter = "channel";
    public const string OsParameter = "os";
    public const string ArchParameter = "arch";

    public const string WindowsOs = "windows";
    public const string X64Arch = "x64";

    public static string Download(string releaseId)
        => $"{DownloadPrefix}/{Uri.EscapeDataString(releaseId)}";

    /// <summary>拼出反馈提交地址：根地址 + 内置路径。</summary>
    public static string BuildFeedbackUrl(Uri baseAddress) => Root(baseAddress) + Feedback;

    /// <summary>拼出管理页的单条反馈地址。</summary>
    public static string AdminFeedbackItem(string id)
        => $"{AdminFeedback}/{Uri.EscapeDataString(id)}";

    /// <summary>拼出单条反馈的日志下载地址。</summary>
    public static string AdminFeedbackLog(string id)
        => $"{AdminFeedbackItem(id)}/log";

    /// <summary>拼出检测地址：根地址 + 内置路径 + 查询参数。</summary>
    public static string BuildCheckUrl(
        Uri baseAddress,
        string currentVersion,
        string channel,
        string os = WindowsOs,
        string arch = X64Arch)
    {
        var root = Root(baseAddress);
        return $"{root}{Check}?{CurrentParameter}={Uri.EscapeDataString(currentVersion)}" +
               $"&{ChannelParameter}={Uri.EscapeDataString(channel)}" +
               $"&{OsParameter}={Uri.EscapeDataString(os)}" +
               $"&{ArchParameter}={Uri.EscapeDataString(arch)}";
    }

    /// <summary>把服务端返回的下载地址（可为相对路径）解析成绝对地址。</summary>
    public static Uri? ResolveDownloadUri(Uri baseAddress, string? downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl)) return null;
        if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out var absolute))
        {
            return absolute.Scheme is "http" or "https" ? absolute : null;
        }

        var root = Root(baseAddress);
        var path = downloadUrl.StartsWith('/') ? downloadUrl : "/" + downloadUrl;
        return Uri.TryCreate(root + path, UriKind.Absolute, out var combined) ? combined : null;
    }

    /// <summary>根地址去掉末尾斜杠，保证拼接后不会出现双斜杠。</summary>
    private static string Root(Uri baseAddress)
        => baseAddress.GetLeftPart(UriPartial.Path).TrimEnd('/');
}
