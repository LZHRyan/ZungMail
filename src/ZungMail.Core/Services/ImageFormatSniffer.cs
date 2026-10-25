using System;

namespace ZungMail.Core.Services;

/// <summary>
/// 按文件头猜图片扩展名。
///
/// 正文里的图片没有文件名（data: 或虚拟主机地址），「另存为」要给出合理默认名只能看魔数。
/// 纯逻辑、无 UI 依赖，单测直接覆盖。
/// </summary>
public static class ImageFormatSniffer
{
    /// <summary>返回形如 <c>.png</c> 的扩展名；识别不了时按 .png 兜底。</summary>
    public static string GuessExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return ".png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return ".gif";
        }

        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return ".bmp";
        }

        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return ".webp";
        }

        return ".png";
    }
}
