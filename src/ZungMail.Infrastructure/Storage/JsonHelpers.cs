using System.Text.Json;
using ZungMail.Core.Domain;

namespace ZungMail.Infrastructure.Storage;

/// <summary>JSON 列的序列化/反序列化辅助（收件人列表、References、附件路径等）。</summary>
internal static class JsonHelpers
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public static string SerializeAddresses(IReadOnlyList<EmailAddress>? list) =>
        list is null || list.Count == 0 ? "[]" : JsonSerializer.Serialize(list, Options);

    public static List<EmailAddress> DeserializeAddresses(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<EmailAddress>();
        try
        {
            return JsonSerializer.Deserialize<List<EmailAddress>>(json, Options) ?? new List<EmailAddress>();
        }
        catch (JsonException)
        {
            return new List<EmailAddress>();
        }
    }

    public static string SerializeStrings(IReadOnlyList<string>? list) =>
        list is null || list.Count == 0 ? "[]" : JsonSerializer.Serialize(list, Options);

    public static List<string> DeserializeStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Options) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    public static string SerializeAttachments(IReadOnlyList<AttachmentInfo>? list) =>
        list is null || list.Count == 0 ? "[]" : JsonSerializer.Serialize(list, Options);

    public static List<AttachmentInfo> DeserializeAttachments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<AttachmentInfo>();
        try
        {
            return JsonSerializer.Deserialize<List<AttachmentInfo>>(json, Options) ?? new List<AttachmentInfo>();
        }
        catch (JsonException)
        {
            return new List<AttachmentInfo>();
        }
    }
}
