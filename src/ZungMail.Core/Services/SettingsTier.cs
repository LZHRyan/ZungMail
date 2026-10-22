namespace ZungMail.Core.Services;

/// <summary>
/// 设置页「档位下拉」的取值辅助：配置文件被手工改成非档位值时，
/// 界面要落到最接近的档位，而不是出现空白项（<c>SelectedIndex = -1</c>）。
/// </summary>
public static class SettingsTier
{
    /// <summary>取最接近 <paramref name="value"/> 的档位下标；空数组返回 -1。</summary>
    public static int NearestIndex(IReadOnlyList<int> options, int value)
    {
        if (options is null || options.Count == 0) return -1;

        var nearest = 0;
        for (var i = 1; i < options.Count; i++)
        {
            var current = Math.Abs((long)options[i] - value);
            var best = Math.Abs((long)options[nearest] - value);
            if (current < best) nearest = i;
        }

        return nearest;
    }
}
