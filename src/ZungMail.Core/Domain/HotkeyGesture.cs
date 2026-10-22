namespace ZungMail.Core.Domain;

/// <summary>全局热键的修饰键组合。</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8
}

/// <summary>
/// 全局热键描述（如 <c>Ctrl+Alt+M</c>）。解析与规范化放在 Core，便于单元测试；
/// 与 Win32 的 VK / MOD_* 映射由宿主层完成。
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>规范显示形式，固定顺序 Ctrl + Alt + Shift + Win + Key。</summary>
    public string Display
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(Key);
            return string.Join("+", parts);
        }
    }

    /// <summary>
    /// 解析用户输入。要求至少一个修饰键 + 一个主键，避免抢占普通按键。
    /// 支持 Ctrl/Control、Alt、Shift、Win/Windows，主键 A-Z、0-9、F1-F24。
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var tokens = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return false;

        var modifiers = HotkeyModifiers.None;
        string? key = null;

        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "win":
                case "windows":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }

            if (key is not null) return false;       // 只允许一个主键
            if (!IsSupportedKey(token)) return false;
            key = token.ToUpperInvariant();
        }

        if (key is null || modifiers == HotkeyModifiers.None) return false;

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    private static bool IsSupportedKey(string token)
    {
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            return c is >= 'A' and <= 'Z' or >= '0' and <= '9';
        }

        if (token.Length is 2 or 3 && char.ToUpperInvariant(token[0]) == 'F'
            && int.TryParse(token[1..], out var number))
        {
            return number is >= 1 and <= 24;
        }

        return false;
    }
}
