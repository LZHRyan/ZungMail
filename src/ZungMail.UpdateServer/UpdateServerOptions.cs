namespace ZungMail.UpdateServer;

/// <summary>
/// 升级服务运行配置。全部可通过环境变量覆盖，便于 Docker 部署：
/// <c>UPDATESERVER_DATA</c>、<c>UPDATESERVER_ADMIN_PASSWORD</c>、<c>UPDATESERVER_MAX_UPLOAD_BYTES</c>，
/// 反馈相关还有 <c>UPDATESERVER_MAX_FEEDBACK_BYTES</c>、<c>UPDATESERVER_MAX_FEEDBACK_RECORDS</c>、
/// <c>UPDATESERVER_FEEDBACK_PER_HOUR</c>。
/// </summary>
public sealed class UpdateServerOptions
{
    /// <summary>数据目录：版本记录 JSON、安装包、管理员口令哈希都在这里，Docker 里挂卷。</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>环境变量提供的管理员口令；为空时走首启生成 + 可修改口令。</summary>
    public string? AdminPassword { get; set; }

    /// <summary>单个安装包上传上限，默认 2 GB。</summary>
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>允许的安装包扩展名（完整安装包）。</summary>
    public string[] AllowedExtensions { get; set; } = [".exe", ".msi", ".zip"];

    /// <summary>管理会话有效期（滑动续期）。</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>登录失败多少次后进入冷却。</summary>
    public int LoginFailureLimit { get; set; } = 5;

    /// <summary>登录冷却时长。</summary>
    public TimeSpan LoginLockout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>单条反馈请求体上限（与客户端约定一致，超过直接 413）。</summary>
    public int MaxFeedbackBytes { get; set; } = ZungMail.Update.Protocol.UpdateRoutes.MaxFeedbackBytes;

    /// <summary>单条反馈日志上限：超出只保留末尾片段。</summary>
    public int MaxFeedbackLogBytes { get; set; } = ZungMail.Update.Protocol.UpdateRoutes.MaxFeedbackLogBytes;

    /// <summary>反馈保留条数上限，超出按时间淘汰最旧记录。</summary>
    public int MaxFeedbackRecords { get; set; } = 200;

    /// <summary>同一来源每小时最多提交多少条反馈；0 表示不限流。</summary>
    public int FeedbackPerHourLimit { get; set; } = 10;

    public static UpdateServerOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new UpdateServerOptions();

        var data = configuration["UPDATESERVER_DATA"]
                   ?? configuration["UpdateServer:DataDirectory"];
        if (!string.IsNullOrWhiteSpace(data)) options.DataDirectory = data.Trim();

        var password = configuration["UPDATESERVER_ADMIN_PASSWORD"];
        if (!string.IsNullOrWhiteSpace(password)) options.AdminPassword = password;

        if (long.TryParse(configuration["UPDATESERVER_MAX_UPLOAD_BYTES"], out var maxBytes) && maxBytes > 0)
        {
            options.MaxUploadBytes = maxBytes;
        }

        if (int.TryParse(configuration["UPDATESERVER_MAX_FEEDBACK_BYTES"], out var feedbackBytes) && feedbackBytes > 0)
        {
            options.MaxFeedbackBytes = feedbackBytes;
        }

        if (int.TryParse(configuration["UPDATESERVER_MAX_FEEDBACK_RECORDS"], out var feedbackRecords) && feedbackRecords > 0)
        {
            options.MaxFeedbackRecords = feedbackRecords;
        }

        // 0 是有效值 = 关闭限流，所以这里允许 >= 0
        if (int.TryParse(configuration["UPDATESERVER_FEEDBACK_PER_HOUR"], out var feedbackPerHour) && feedbackPerHour >= 0)
        {
            options.FeedbackPerHourLimit = feedbackPerHour;
        }

        return options;
    }
}
