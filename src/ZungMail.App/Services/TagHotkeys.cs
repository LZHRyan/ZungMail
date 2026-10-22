using System;
using System.Windows.Input;
using ZungMail.Core.Services;

namespace ZungMail.App.Services;

/// <summary>
/// 标签快捷键：WPF 按键 → 单键 token（1-9 / A-Z / F1-F12）。
/// 只做「无修饰键」的映射；Ctrl / Alt / Shift / Win 组合留给应用自身快捷键。
/// </summary>
public static class TagHotkeys
{
    /// <summary>把 WPF 按键转成单键 token；不支持的键返回 false。</summary>
    public static bool TryGetToken(Key key, out string token)
    {
        token = "";

        if (key is >= Key.A and <= Key.Z)
        {
            token = ((char)('A' + (key - Key.A))).ToString();
        }
        else if (key is >= Key.D0 and <= Key.D9)
        {
            token = ((char)('0' + (key - Key.D0))).ToString();
        }
        else if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            token = ((char)('0' + (key - Key.NumPad0))).ToString();
        }
        else if (key is >= Key.F1 and <= Key.F24)
        {
            token = "F" + (key - Key.F1 + 1);
        }
        else
        {
            return false;
        }

        // 只放行真正可绑定的键（F5 刷新、F1 预留都不算）
        return TagShortcutKeys.IsAllowed(token);
    }
}
