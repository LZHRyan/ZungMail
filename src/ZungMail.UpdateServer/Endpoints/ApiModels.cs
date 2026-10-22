using System.Text.Json.Serialization;

namespace ZungMail.UpdateServer.Endpoints;

/// <summary>管理页与后端之间的 JSON 形状（只用于后台，客户端不依赖）。</summary>
public sealed class AdminReleaseView
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

    [JsonPropertyName("minVersion")]
    public string? MinVersion { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("published")]
    public bool Published { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("downloadCount")]
    public long DownloadCount { get; set; }

    [JsonPropertyName("fileExists")]
    public bool FileExists { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = "";
}

public sealed class AdminLoginRequest
{
    [JsonPropertyName("password")]
    public string? Password { get; set; }
}

public sealed class AdminSessionView
{
    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonPropertyName("passwordSource")]
    public string PasswordSource { get; set; } = "";

    [JsonPropertyName("canChangePassword")]
    public bool CanChangePassword { get; set; }
}

public sealed class AdminReleaseUpdateRequest
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }

    [JsonPropertyName("minVersion")]
    public string? MinVersion { get; set; }
}

public sealed class AdminPasswordChangeRequest
{
    [JsonPropertyName("currentPassword")]
    public string? CurrentPassword { get; set; }

    [JsonPropertyName("newPassword")]
    public string? NewPassword { get; set; }
}

public sealed class AdminSettingsView
{
    [JsonPropertyName("dataDirectory")]
    public string DataDirectory { get; set; } = "";

    [JsonPropertyName("passwordSource")]
    public string PasswordSource { get; set; } = "";

    [JsonPropertyName("canChangePassword")]
    public bool CanChangePassword { get; set; }

    [JsonPropertyName("initialPasswordFile")]
    public string? InitialPasswordFile { get; set; }

    [JsonPropertyName("checkUrlHint")]
    public string CheckUrlHint { get; set; } = "";

    [JsonPropertyName("maxUploadBytes")]
    public long MaxUploadBytes { get; set; }
}

public sealed class AdminReleasesView
{
    [JsonPropertyName("releases")]
    public List<AdminReleaseView> Releases { get; set; } = [];
}

/// <summary>反馈列表项：只带摘要与规模信息，完整描述与日志走详情接口。</summary>
public sealed class AdminFeedbackView
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("categoryLabel")]
    public string CategoryLabel { get; set; } = "";

    [JsonPropertyName("descriptionPreview")]
    public string DescriptionPreview { get; set; } = "";

    [JsonPropertyName("contact")]
    public string? Contact { get; set; }

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = "";

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("logBytes")]
    public long LogBytes { get; set; }

    [JsonPropertyName("hasLog")]
    public bool HasLog { get; set; }

    [JsonPropertyName("logTruncated")]
    public bool LogTruncated { get; set; }
}

public sealed class AdminFeedbackListView
{
    [JsonPropertyName("items")]
    public List<AdminFeedbackView> Items { get; set; } = [];
}

/// <summary>反馈详情：完整描述、环境摘要与日志正文。</summary>
public sealed class AdminFeedbackDetailView
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("categoryLabel")]
    public string CategoryLabel { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("contact")]
    public string? Contact { get; set; }

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = "";

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("log")]
    public string? Log { get; set; }

    [JsonPropertyName("logTruncated")]
    public bool LogTruncated { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}
