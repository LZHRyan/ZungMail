using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZungMail.App.Services;

/// <summary>
/// 托盘右键菜单的绘制资源：从 WPF 主题里取色，保证与主界面同一套配色。
/// 每次右键都重新读取，主题切换后托盘菜单也会跟着变。
/// </summary>
internal sealed record TrayMenuColors(Color Background, Color Border, Color Hover, Color Text, Color Muted, Color Separator)
{
    public static TrayMenuColors FromTheme()
    {
        var app = System.Windows.Application.Current;
        return new TrayMenuColors(
            Read(app, "WindowBackgroundBrush"),
            Read(app, "RuleStrongBrush"),
            Read(app, "HoverBrush"),
            Read(app, "InkBrush"),
            Read(app, "MutedBrush"),
            Read(app, "RuleBrush"));
    }

    private static Color Read(System.Windows.Application app, string key)
    {
        if (app.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
        {
            var c = brush.Color;
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        return Color.FromArgb(255, 255, 255, 255);
    }
}

/// <summary>
/// 卡通用的圆角菜单容器：WinForms 的 ContextMenuStrip 默认是方角、大字号，
/// 这里用区域裁出圆角，并把背景/边框/悬停交给 <see cref="TrayMenuRenderer"/> 绘制。
/// </summary>
internal sealed class TrayMenuStrip : ContextMenuStrip
{
    private const int CornerRadius = 10;

    public TrayMenuStrip(TrayMenuColors colors)
    {
        Colors = colors;
        ShowImageMargin = true;
        ShowCheckMargin = false;
        DropShadowEnabled = false;
        BackColor = colors.Background;
        ForeColor = colors.Text;
        Padding = new Padding(5);
        // 13px ≈ 9.75pt，与主界面命令文本一致
        Font = new Font("Microsoft YaHei UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);
        Renderer = new TrayMenuRenderer(this);
    }

    public TrayMenuColors Colors { get; private set; }

    /// <summary>图标按设备像素烘焙：菜单用 ImageScaling=None 原样绘制，必须与 DPI 匹配。</summary>
    public int IconPixels { get; set; } = 14;

    public void ApplyColors(TrayMenuColors colors)
    {
        Colors = colors;
        BackColor = colors.Background;
        ForeColor = colors.Text;
    }

    /// <summary>
    /// 主题变了就整体刷新：背景、边框、悬停块、文字色与图标都跟着换。
    /// 图标是按当前主题颜色烘焙的位图，必须重建，否则深色下会看不清。
    /// </summary>
    public void RefreshTheme(TrayMenuColors colors)
    {
        ApplyColors(colors);
        Renderer = new TrayMenuRenderer(this);

        foreach (ToolStripItem item in Items)
        {
            item.ForeColor = colors.Text;
            if (item is not ToolStripMenuItem menuItem) continue;

            if (menuItem.Tag is string[] keys && keys.Length == 2)
            {
                var old = menuItem.Image;
                menuItem.Image = TrayMenuIcons.Create(keys[0], keys[1], IconPixels);
                old?.Dispose();
            }
        }

        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        TryApplyRegion();
    }

    /// <summary>
    /// 整窗裁成圆角：方角窗口在浅色桌面上很突兀。
    /// 任何异常都退回方角菜单，绝不能因为圆角导致菜单不显示。
    /// </summary>
    private void TryApplyRegion()
    {
        if (Width < 24 || Height < 24) return;

        try
        {
            using var path = TrayMenuGraphics.RoundedRect(new Rectangle(0, 0, Width, Height), CornerRadius);
            var region = new Region(path);
            var previous = Region;
            Region = region;
            previous?.Dispose();
        }
        catch
        {
            // 圆角失败不影响菜单可用
        }
    }
}

/// <summary>菜单渲染：圆角背景、细边框、圆角悬停块、居中分隔线与主题色文本。</summary>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly TrayMenuStrip _menu;

    /// <summary>最近绘制的图标右边缘：文字紧接其后 10px，避免默认图片留白把间距拉得太开。</summary>
    private int _lastImageRight = -1;

    public TrayMenuRenderer(TrayMenuStrip menu) : base(new TrayMenuColorTable(menu.Colors))
    {
        _menu = menu;
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var path = TrayMenuGraphics.RoundedRect(rect, 10);
        using var brush = new SolidBrush(_menu.Colors.Background);
        g.FillPath(brush, path);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var path = TrayMenuGraphics.RoundedRect(rect, 10);
        using var pen = new Pen(_menu.Colors.Border);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = TrayMenuGraphics.RoundedRect(rect, 6);
        using var brush = new SolidBrush(_menu.Colors.Hover);
        g.FillPath(brush, path);
    }

    /// <summary>
    /// 图标与文字都按「整行高度」垂直居中绘制。
    /// 交给 ToolStrip 默认布局时，图片按内容区居中、文字按字体矩形绘制，两者会差 1~3px。
    /// </summary>
    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is null || e.ImageRectangle.Width <= 0)
        {
            base.OnRenderItemImage(e);
            return;
        }

        var x = e.ImageRectangle.X + (e.ImageRectangle.Width - e.Image.Width) / 2;
        var y = (e.Item.Height - e.Image.Height) / 2;
        e.Graphics.DrawImage(e.Image, new Rectangle(x, y, e.Image.Width, e.Image.Height));
        _lastImageRight = x + e.Image.Width;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            base.OnRenderItemText(e);
            return;
        }

        var color = e.Item.Enabled ? _menu.Colors.Text : _menu.Colors.Muted;

        // 有图标时紧接图标右边缘 +10px 起排；没有图标（极少见）就退回默认位置
        var textX = e.Item.Image is not null && _lastImageRight > 0
            ? _lastImageRight + 10
            : e.TextRectangle.X;
        var width = Math.Max(0, e.Item.Width - textX - 12);
        var rect = new Rectangle(textX, 0, width, e.Item.Height);

        TextRenderer.DrawText(
            e.Graphics,
            e.Text,
            e.TextFont,
            rect,
            color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(_menu.Colors.Separator);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = _menu.Colors.Text;
        base.OnRenderArrow(e);
    }
}

/// <summary>让下拉菜单的图片留白区与卡片同色，避免默认的灰色竖条。</summary>
internal sealed class TrayMenuColorTable : ProfessionalColorTable
{
    private readonly TrayMenuColors _colors;

    public TrayMenuColorTable(TrayMenuColors colors) => _colors = colors;

    public override Color ToolStripDropDownBackground => _colors.Background;
    public override Color ImageMarginGradientBegin => _colors.Background;
    public override Color ImageMarginGradientMiddle => _colors.Background;
    public override Color ImageMarginGradientEnd => _colors.Background;
    public override Color MenuBorder => _colors.Border;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => _colors.Hover;
    public override Color MenuItemSelectedGradientBegin => _colors.Hover;
    public override Color MenuItemSelectedGradientEnd => _colors.Hover;
    public override Color SeparatorDark => _colors.Separator;
    public override Color SeparatorLight => _colors.Separator;
}

internal static class TrayMenuGraphics
{
    /// <summary>圆角矩形路径（半径自动收窄到尺寸的一半）。</summary>
    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var r = Math.Max(1, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        var d = r * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// 把主界面的矢量图标渲染成菜单位图，避免另画一套像素图标。
/// 尺寸按「逻辑 14px × 当前 DPI」直接烘焙成设备像素：菜单用 ImageScaling=None 原样绘制，
/// 不再出现图标比文字大一圈的情况。
/// </summary>
internal static class TrayMenuIcons
{
    /// <summary>菜单图标的逻辑尺寸（比正文略大一点，过大显得笨重）。</summary>
    public const int LogicalSize = 14;

    public static int PixelsFor(double dpiScale) => Math.Max(10, (int)Math.Round(LogicalSize * dpiScale));

    /// <summary>当前系统 DPI 缩放（1.0 = 100%，1.5 = 150%）。</summary>
    public static double SystemDpiScale()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }

    public static Image? Create(string iconKey, string brushKey, int pixelSize)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.TryFindResource(iconKey) is not System.Windows.Media.Geometry geometry) return null;

            var color = TrayMenuColors.FromTheme();
            var stroke = app.TryFindResource(brushKey) is System.Windows.Media.SolidColorBrush b
                ? System.Windows.Media.Color.FromArgb(b.Color.A, b.Color.R, b.Color.G, b.Color.B)
                : System.Windows.Media.Color.FromRgb(color.Text.R, color.Text.G, color.Text.B);

            // 逻辑图标是 24×24，按目标像素缩放；描边在小尺寸下略微加粗，避免发虚
            var scale = pixelSize / 24.0;
            var penWidth = Math.Max(1.2, 1.9 * scale);

            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new System.Windows.Media.ScaleTransform(scale, scale));
                var pen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(stroke), penWidth)
                {
                    StartLineCap = System.Windows.Media.PenLineCap.Round,
                    EndLineCap = System.Windows.Media.PenLineCap.Round,
                    LineJoin = System.Windows.Media.PenLineJoin.Round
                };
                dc.DrawGeometry(null, pen, geometry);
                dc.Pop();
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                pixelSize, pixelSize, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = new System.IO.MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            return Image.FromStream(stream);
        }
        catch
        {
            // 取不到图标不影响菜单可用性
            return null;
        }
    }
}
