using System;
using System.Windows;
using System.Windows.Controls;

namespace ZungMail.App.Controls;

/// <summary>
/// 附件类型徽标：按文件名扩展名取专属矢量图标与底色，未知格式回落到默认文件图标。
/// 阅读区附件卡片与撰写窗口附件条共用，保证图标风格与尺寸比例一致。
/// </summary>
public partial class AttachmentIcon : UserControl
{
    public static readonly DependencyProperty FileNameProperty =
        DependencyProperty.Register(
            nameof(FileName),
            typeof(string),
            typeof(AttachmentIcon),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(double),
            typeof(AttachmentIcon),
            new PropertyMetadata(34d, OnIconSizeChanged));

    public AttachmentIcon()
    {
        InitializeComponent();
        ApplySize();
    }

    /// <summary>用于判定文件类型的文件名（含扩展名）。</summary>
    public string FileName
    {
        get => (string)GetValue(FileNameProperty);
        set => SetValue(FileNameProperty, value);
    }

    /// <summary>徽标边长；圆角与内部图标按比例缩放。</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    private static void OnIconSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AttachmentIcon)d).ApplySize();

    private void ApplySize()
    {
        var size = IconSize;
        IconGrid.Width = size;
        IconGrid.Height = size;
        Badge.CornerRadius = new CornerRadius(size * 0.265);
        GlyphMark.Width = Math.Round(size * 0.6, 1);
        GlyphMark.Height = Math.Round(size * 0.6, 1);
    }
}
