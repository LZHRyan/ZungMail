using System.Text.Json.Serialization;

namespace ZungMail.UpdateServer.Storage;

/// <summary>一个发布记录（含草稿）。序列化进 <c>releases.json</c>，字段只增不改。</summary>
public sealed class ReleaseRecord
{
    public string Id { get; set; } = "";

    /// <summary>规范化版本号（三段或四段数字，可带预发布后缀）。</summary>
    public string Version { get; set; } = "";

    public string Channel { get; set; } = "stable";

    public string Notes { get; set; } = "";

    public bool Mandatory { get; set; }

    public string? MinVersion { get; set; }

    /// <summary>上传时的原始文件名（下载时作为 Content-Disposition 文件名）。</summary>
    public string FileName { get; set; } = "";

    /// <summary>相对数据目录的存储路径，避免数据目录整体迁移后失效。</summary>
    public string RelativePath { get; set; } = "";

    public long Size { get; set; }

    public string Sha256 { get; set; } = "";

    public bool Published { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public long DownloadCount { get; set; }

    public string? UploadedBy { get; set; }

    [JsonIgnore]
    public string DisplayVersion => Version;

    public ReleaseRecord Clone() => (ReleaseRecord)MemberwiseClone();
}

/// <summary>`releases.json` 的根对象：带格式版本，后续可平滑加字段。</summary>
public sealed class ReleaseCatalogFile
{
    public int Format { get; set; } = 1;

    public List<ReleaseRecord> Releases { get; set; } = [];
}
