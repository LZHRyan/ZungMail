using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZungMail.Update.Protocol;

/// <summary>升级协议 DTO 与共享的 JSON 选项（客户端解析、服务端输出都用它）。</summary>
public static class UpdateJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>`GET /api/update/check` 的响应：没有可用更新时 <see cref="Update"/> 为 null。</summary>
public sealed class UpdateCheckResponse
{
    [JsonPropertyName("protocol")]
    public int Protocol { get; set; } = UpdateRoutes.ProtocolVersion;

    [JsonPropertyName("serverTime")]
    public DateTimeOffset ServerTime { get; set; }

    [JsonPropertyName("update")]
    public UpdateReleaseInfo? Update { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>一个已发布版本的完整信息（客户端据此展示并下载）。</summary>
public sealed class UpdateReleaseInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "stable";

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }

    /// <summary>低于该版本的客户端必须升级到本版本（空表示不限制）。</summary>
    [JsonPropertyName("minVersion")]
    public string? MinVersion { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset PublishedAt { get; set; }

    /// <summary>相对路径（推荐，兼容反向代理前缀）或绝对地址。</summary>
    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = "";
}

/// <summary>`GET /api/update/ping` 的响应：只配了根地址时先确认服务可用。</summary>
public sealed class UpdatePingResponse
{
    [JsonPropertyName("protocol")]
    public int Protocol { get; set; } = UpdateRoutes.ProtocolVersion;

    [JsonPropertyName("service")]
    public string Service { get; set; } = UpdateRoutes.ServiceName;

    [JsonPropertyName("serverTime")]
    public DateTimeOffset ServerTime { get; set; }
}

/// <summary>错误响应：客户端用它把服务端原因展示到界面。</summary>
public sealed class UpdateErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}

/// <summary>
/// 安装产物里可选的 `update.json`：让运维在打包阶段就把默认升级服务地址带进客户端。
/// 用户设置优先于本文件。
/// </summary>
public sealed class UpdateBootstrapConfig
{
    [JsonPropertyName("updateServiceUrl")]
    public string? UpdateServiceUrl { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }
}

/// <summary>反馈分类：客户端与升级服务共用同一组取值与文案，避免两边对不上。</summary>
public static class FeedbackCategories
{
    public const string Malfunction = "malfunction";
    public const string Question = "question";
    public const string Suggestion = "suggestion";
    public const string Other = "other";

    public static readonly string[] All = [Malfunction, Question, Suggestion, Other];

    /// <summary>未知或空值统一归为「其他」，服务端据此保证落盘数据可控。</summary>
    public static string Normalize(string? value)
    {
        var trimmed = (value ?? "").Trim().ToLowerInvariant();
        return All.Contains(trimmed) ? trimmed : Other;
    }

    public static string Label(string? value) => Normalize(value) switch
    {
        Malfunction => "功能异常",
        Question => "使用咨询",
        Suggestion => "改进建议",
        _ => "其他"
    };
}

/// <summary>
/// `POST /api/feedback` 的请求体：分类、描述与联系方式由用户填写，
/// 版本 / 环境 / 日志是客户端按需附加的诊断信息。
/// </summary>
public sealed class FeedbackRequest
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = FeedbackCategories.Other;

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>可选联系方式；用户可以留空。</summary>
    [JsonPropertyName("contact")]
    public string? Contact { get; set; }

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = "";

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    /// <summary>多行「键：值」文本；不含邮箱、口令与正文。</summary>
    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    /// <summary>脱敏后的日志末尾片段；未附加时为 null。</summary>
    [JsonPropertyName("log")]
    public string? Log { get; set; }

    /// <summary>日志是否因体积限制被截断。</summary>
    [JsonPropertyName("logTruncated")]
    public bool LogTruncated { get; set; }

    [JsonPropertyName("submittedAt")]
    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>序列化后的字节数：客户端用它判断是否需要继续截断日志。</summary>
    public int SerializedSize() => JsonSerializer.SerializeToUtf8Bytes(this, UpdateJson.Options).Length;
}

/// <summary>`POST /api/feedback` 的响应：调用方展示 <see cref="Id"/> 便于后续追查。</summary>
public sealed class FeedbackResponse
{
    [JsonPropertyName("protocol")]
    public int Protocol { get; set; } = UpdateRoutes.ProtocolVersion;

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("receivedAt")]
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>
/// 把反馈请求渲染成可直接粘贴到邮件 / 聊天里的纯文本（离线降级与预览都用它）。
/// </summary>
public static class FeedbackText
{
    public static string ToPlainText(FeedbackRequest request)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("【ZungMail 问题反馈】");
        builder.AppendLine("分类：" + FeedbackCategories.Label(request.Category));
        builder.AppendLine("客户端：" + (string.IsNullOrWhiteSpace(request.ClientVersion) ? "未知" : request.ClientVersion)
                           + (string.IsNullOrWhiteSpace(request.Channel) ? "" : $"（{request.Channel}）"));
        builder.AppendLine("提交时间：" + request.SubmittedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine("联系方式：" + (string.IsNullOrWhiteSpace(request.Contact) ? "（未填写）" : request.Contact.Trim()));
        builder.AppendLine();
        builder.AppendLine("问题描述：");
        builder.AppendLine(request.Description.Trim());
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.Environment))
        {
            builder.AppendLine("系统环境：");
            builder.AppendLine(request.Environment.Trim());
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(request.Log))
        {
            builder.AppendLine(request.LogTruncated ? "日志（已截断）：" : "日志：");
            builder.AppendLine(request.Log);
        }
        else
        {
            builder.AppendLine("日志：（未附加）");
        }

        return builder.ToString().TrimEnd();
    }
}
