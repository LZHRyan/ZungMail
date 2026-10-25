using System;
using System.Text.Json;

namespace ZungMail.Core.Services;

/// <summary>
/// 正文里双击图片时，WebView2 注入脚本回传的消息。
///
/// 纯逻辑：只负责解析与校验，取字节、解码、开窗口都在 App 层。
/// 拆到这里是为了让「消息格式」能被单元测试直接守住——脚本与宿主分处 JS / C#，
/// 一旦字段名漂移，双击就会静默失效。
/// </summary>
/// <param name="Source">图片地址（data: / 虚拟主机 / file:///）。</param>
/// <param name="AltText">alt 属性，用作窗口标题的兜底文案。</param>
/// <param name="NaturalWidth">原始像素宽，0 表示脚本没给。</param>
/// <param name="NaturalHeight">原始像素高，0 表示脚本没给。</param>
public sealed record MailBodyImageMessage(string Source, string AltText, int NaturalWidth, int NaturalHeight)
{
    /// <summary>消息类型标记：注入脚本与宿主必须一致。</summary>
    public const string TypeName = "mail-image-dblclick";

    /// <summary>
    /// 解析脚本回传的 JSON。类型不符、缺少地址或格式非法时返回 null——
    /// 正文里的任何意外消息都不应该让阅读界面出错。
    /// </summary>
    public static MailBodyImageMessage? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return null;
            if (!string.Equals(type.GetString(), TypeName, StringComparison.Ordinal)) return null;

            if (!root.TryGetProperty("src", out var source) || source.ValueKind != JsonValueKind.String) return null;
            var uri = source.GetString();
            if (string.IsNullOrWhiteSpace(uri)) return null;

            return new MailBodyImageMessage(
                uri,
                ReadString(root, "alt"),
                ReadInt(root, "width"),
                ReadInt(root, "height"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)) return 0;

        var value = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(element.GetString(), out var parsed) => parsed,
            _ => 0
        };

        return Math.Max(0, value);
    }
}
