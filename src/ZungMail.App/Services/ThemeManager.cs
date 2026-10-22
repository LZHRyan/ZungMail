using System.Windows;
using ZungMail.Core.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ZungMail.App.Services;

/// <summary>
/// 主题切换：替换 Application 资源中的调色板字典（FR-13 无，属 NFR-U 界面一致性）。
/// 所有引用调色板的样式必须使用 DynamicResource，才能在切换后立即生效。
/// <para>
/// 现在只提供浅色系主题：默认（中性灰 + 系统蓝）、淡蓝、淡粉、淡橙。
/// 暗色模式已下线；HTML 正文/编辑器仍按浅色渲染（暗色链路保留但不再启用）。
/// </para>
/// </summary>
public sealed class ThemeManager
{
    /// <summary>主题 key → 调色板文件；未知值（含历史遗留的 dark）一律回落默认亮色。</summary>
    private static readonly Dictionary<string, string> Palettes = new(StringComparer.OrdinalIgnoreCase)
    {
        [ThemeKeys.Default] = "Themes/Palette.Light.xaml",
        [ThemeKeys.Blue] = "Themes/Palette.Blue.xaml",
        [ThemeKeys.Pink] = "Themes/Palette.Pink.xaml",
        [ThemeKeys.Orange] = "Themes/Palette.Orange.xaml",
    };

    private readonly ISettingsStore _settings;

    public ThemeManager(ISettingsStore settings) => _settings = settings;

    /// <summary>当前主题 key（light / blue / pink / orange）。</summary>
    public string CurrentKey { get; private set; } = ThemeKeys.Default;

    /// <summary>暗色模式已下线；保留该属性给正文渲染链路，恒为 false。</summary>
    public bool IsDark => false;

    /// <summary>应用主题；<paramref name="theme"/> 为 null 时使用设置中的值。</summary>
    public void Apply(string? theme = null, bool persist = false)
    {
        var key = Normalize(theme ?? _settings.Current.Theme);
        SwapPalette(key);
        ApplyFluentLightTheme();
        CurrentKey = key;

        // 历史配置可能是 dark，或手工写成大小写不一致的 key，这里回写规范化后的值
        if (!string.Equals(_settings.Current.Theme, key, StringComparison.Ordinal))
        {
            _settings.Current.Theme = key;
            if (persist) _ = _settings.SaveAsync();
        }
    }

    /// <summary>把任意输入规范成受支持的 key。</summary>
    public static string Normalize(string? theme)
        => theme is not null && Palettes.ContainsKey(theme) ? theme.ToLowerInvariant() : ThemeKeys.Default;

    /// <summary>同步 WPF-UI 的主题资源，让 ContentDialog / Snackbar / ProgressRing 保持浅色。</summary>
    private static void ApplyFluentLightTheme()
    {
        try
        {
            ApplicationThemeManager.Apply(
                ApplicationTheme.Light,
                WindowBackdropType.None,
                updateAccent: false);
        }
        catch (Exception)
        {
            // 设计系统资源尚未加载（例如设计器里预览单个窗口）时忽略，界面仍用自有调色板
        }
    }

    private static void SwapPalette(string key)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var dictionary = new ResourceDictionary
        {
            Source = new Uri(Palettes[key], UriKind.Relative)
        };

        var merged = app.Resources.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++)
        {
            var source = merged[i].Source?.OriginalString ?? string.Empty;
            if (source.Contains("Palette.", StringComparison.OrdinalIgnoreCase))
            {
                merged[i] = dictionary;
                return;
            }
        }

        merged.Insert(0, dictionary);
    }
}

/// <summary>主题 key 常量（设置里存字符串，避免再引入枚举迁移）。</summary>
public static class ThemeKeys
{
    public const string Default = "light";
    public const string Blue = "blue";
    public const string Pink = "pink";
    public const string Orange = "orange";
}
