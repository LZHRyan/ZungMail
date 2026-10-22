using System;
using System.Windows;

namespace ZungMail.App.Views;

/// <summary>
/// 主窗口启动时的尺寸与位置：按「显示器工作区」的 80% 宽高居中，而不是写死 1400×880。
/// 这样在 1080P / 2K / 4K、100%～200% 缩放、任务栏在上下左右时都能拿到合适的初始大小。
/// WPF 的 SystemParameters.WorkArea 已经是 DIP（与显示器 DPI 无关的逻辑像素），
/// 所以这里算出来的宽高在 150% 缩放下同样是「工作区的 80%」。
/// </summary>
public static class WindowStartupBounds
{
    /// <summary>目标占用比例：工作区宽高的 80%（约 64% 面积），四周留出桌面余量。</summary>
    public const double DefaultOccupancy = 0.8;

    /// <summary>按工作区算出窗口矩形（DIP）；返回空矩形表示工作区无效、调用方保持原样。</summary>
    public static Rect ComputeBounds(Rect workArea, double minWidth, double minHeight, double occupancy)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0 || occupancy <= 0) return Rect.Empty;

        // 先按比例放大，再用最小宽高兜底，最后不许超过工作区（屏幕太小或比例过大时退化为满工作区）
        var width = Math.Min(workArea.Width, Math.Max(minWidth, workArea.Width * occupancy));
        var height = Math.Min(workArea.Height, Math.Max(minHeight, workArea.Height * occupancy));

        return new Rect(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height);
    }

    /// <summary>把「工作区 80% 居中」应用到窗口；必须在 Show 之前调用。</summary>
    public static void Apply(Window window, double occupancy = DefaultOccupancy)
    {
        if (window is null) return;
        if (window.WindowState != WindowState.Normal) return;

        var bounds = ComputeBounds(SystemParameters.WorkArea, window.MinWidth, window.MinHeight, occupancy);
        if (bounds.IsEmpty) return;

        // CenterScreen 会把窗口放到整屏中心（含任务栏区域），这里自己算工作区中心更稳
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        window.Left = bounds.X;
        window.Top = bounds.Y;
    }
}
