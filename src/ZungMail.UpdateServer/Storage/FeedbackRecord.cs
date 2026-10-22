using System.Text.Json.Serialization;
using ZungMail.Update.Protocol;

namespace ZungMail.UpdateServer.Storage;

/// <summary>
/// 一条用户反馈的落盘记录。日志单独存成 <c>&lt;id&gt;.log</c>，
/// 列表接口只读这份元数据，不会因为日志很大而拖慢管理页。
/// </summary>
public sealed class FeedbackRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = FeedbackCategories.Other;

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("contact")]
    public string? Contact { get; set; }

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = "";

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    /// <summary>多行「键：值」环境摘要（不含邮箱、口令与正文）。</summary>
    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("logTruncated")]
    public bool LogTruncated { get; set; }

    [JsonPropertyName("logBytes")]
    public long LogBytes { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    public FeedbackRecord Clone() => (FeedbackRecord)MemberwiseClone();
}
