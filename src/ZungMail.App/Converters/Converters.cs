using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ZungMail.Core.Domain;

namespace ZungMail.App.Converters;

/// <summary>十六进制颜色字符串（如 #2563eb）→ 画刷，用于头像底色。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value as string;
        if (string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;
        if (Cache.TryGetValue(hex, out var cached)) return cached;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Cache[hex] = brush;
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>非空（字符串非空 / 数字大于 0 / 非 null）→ Visible。</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            string s => s.Length > 0,
            int i => i > 0,
            _ => true
        };
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>枚举值与 ConverterParameter 相等 → true，用于分段控件的选中态。</summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string name) return false;
        return value is not null &&
               string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name && targetType.IsEnum)
        {
            return Enum.Parse(targetType, name, ignoreCase: true);
        }
        return Binding.DoNothing;
    }
}

/// <summary>资源键字符串 → 资源对象（用于按图标键取几何）。</summary>
public sealed class ResourceKeyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return Binding.DoNothing;
        return System.Windows.Application.Current?.TryFindResource(key) ?? Binding.DoNothing;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>字符串为空 → Visible（用于输入框占位提示）。</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>布尔取反 → 可见性（true 折叠）。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>布尔取反（用于「忙碌时禁用按钮」等场景）。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>传输加密方式 / 凭据类型 / 收取策略 → 中文显示名（账户配置下拉框）。</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SecureMode.Ssl => "SSL / TLS（993 · 465）",
        SecureMode.StartTls => "STARTTLS（143 · 587）",
        SecureMode.None => "不加密（不推荐）",
        AuthKind.AuthCode => "授权码",
        AuthKind.Password => "登录密码",
        AuthKind.OAuth2 => "OAuth2 令牌",
        FetchPolicy.Inherit => "继承默认策略",
        FetchPolicy.Full => "完整收取（正文 + 附件）",
        FetchPolicy.BodyOnly => "仅收取正文（附件按需下载）",
        FetchPolicy.HeadersOnly => "仅收取邮件头（正文按需下载）",
        _ => value?.ToString() ?? string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>附件文件名 → 徽标图标几何 / 底色资源键；未知扩展名统一回落到默认文件图标。</summary>
internal readonly record struct FileTypeVisual(string GlyphKey, string BrushKey)
{
    public static FileTypeVisual Resolve(string? fileName)
    {
        var extension = System.IO.Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "doc" or "docx" or "rtf" or "odt" or "wps" => new("IcoFileDoc", "FileBadgeDocBrush"),
            "xls" or "xlsx" or "csv" or "ods" or "et" => new("IcoFileXls", "FileBadgeXlsBrush"),
            "ppt" or "pptx" or "odp" or "dps" => new("IcoFilePpt", "FileBadgePptBrush"),
            "pdf" => new("IcoFilePdf", "FileBadgePdfBrush"),
            "txt" or "md" or "log" or "ini" or "cfg" or "json" or "xml" or "html" or "htm" => new("IcoFileTxt", "FileBadgeTxtBrush"),
            "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" or "tif" or "tiff" or "svg" or "heic" or "ico"
                => new("IcoFileImage", "FileBadgeImageBrush"),
            "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz" or "iso" => new("IcoFileZip", "FileBadgeZipBrush"),
            "mp3" or "wav" or "flac" or "aac" or "m4a" or "ogg" or "wma" or "ape" => new("IcoFileAudio", "FileBadgeAudioBrush"),
            "mp4" or "avi" or "mkv" or "mov" or "wmv" or "flv" or "webm" or "m4v" or "mpg" or "mpeg" or "rmvb"
                => new("IcoFileVideo", "FileBadgeVideoBrush"),
            _ => new("IcoFileGeneric", "FileBadgeDefaultBrush")
        };
    }
}

/// <summary>附件文件名 → 白色描边的类型图标几何。</summary>
public sealed class FileTypeGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Application.Current?.TryFindResource(FileTypeVisual.Resolve(value as string).GlyphKey) ?? Binding.DoNothing;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>附件文件名 → 类型徽标底色。</summary>
public sealed class FileTypeBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Windows.Application.Current?.TryFindResource(FileTypeVisual.Resolve(value as string).BrushKey) ?? Binding.DoNothing;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

