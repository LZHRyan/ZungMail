using System;
using System.Windows;

namespace ZungMail.App.Views;

/// <summary>子窗体尺寸计算：按当前主窗体（owner）的实际宽高取比例，并用子窗体最小尺寸兜底。</summary>
public static class WindowSizing
{
    /// <summary>写信 / 编辑邮件的默认尺寸：当前主窗体宽高的 85%。</summary>
    public const double ComposeOwnerRatio = 0.85;

    /// <summary>双击弹出的独立阅读窗口默认尺寸：同样取当前主窗体宽高的 85%。</summary>
    public const double ReaderOwnerRatio = 0.85;

    /// <summary>按 owner 当前尺寸计算比例尺寸；owner 尺寸无效时返回空尺寸。</summary>
    public static Size OwnerRelative(
        double ownerWidth,
        double ownerHeight,
        double minWidth,
        double minHeight,
        double ratio)
    {
        if (ownerWidth <= 0 || ownerHeight <= 0 || ratio <= 0) return Size.Empty;

        return new Size(
            Math.Max(minWidth, ownerWidth * ratio),
            Math.Max(minHeight, ownerHeight * ratio));
    }
}
