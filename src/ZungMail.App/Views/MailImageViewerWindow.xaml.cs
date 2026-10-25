using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ZungMail.Core.Services;

namespace ZungMail.App.Views;

/// <summary>
/// 邮件正文图片的放大查看窗口：双击正文图片打开。
///
/// 用 WPF 的 <see cref="Image"/> 而不是再开一个 WebView2——正文里的图片要么是 data:，
/// 要么已经落成本地附件文件，宿主手上就有字节；这里直接解码显示，省掉一份渲染进程。
/// 支持滚轮缩放、按住左键拖动平视、双击在「适应窗口 / 实际大小」之间切换，Esc 关闭。
/// </summary>
public partial class MailImageViewerWindow : DialogWindow
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 8.0;

    private readonly BitmapSource _image;
    private readonly byte[] _bytes;
    private readonly string _fileName;

    private double _zoom = 1.0;
    private bool _fitted;

    // 拖动平视：记录按下时的指针位置与当时的滚动偏移
    private bool _panning;
    private Point _panOrigin;
    private double _panOffsetX;
    private double _panOffsetY;

    public MailImageViewerWindow(BitmapSource image, byte[] bytes, string fileName, string? altText)
    {
        InitializeComponent();

        _image = image;
        _bytes = bytes;
        _fileName = string.IsNullOrWhiteSpace(fileName) ? "image" : fileName;

        Preview.Source = image;

        var title = string.IsNullOrWhiteSpace(altText) ? "查看图片" : altText;
        Title = title;
        TitleText.Text = title;

        Loaded += OnViewerLoaded;
        SizeChanged += (_, _) => UpdateStatus();
        ContentRendered += (_, _) => FitToWindow();
    }

    // ---------------- 生命周期 ----------------

    private void OnViewerLoaded(object sender, RoutedEventArgs e)
    {
        UpdateStatus();
        FitToWindow();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    // ---------------- 缩放 ----------------

    private void FitToWindow()
    {
        var viewportWidth = Scroller.ViewportWidth;
        var viewportHeight = Scroller.ViewportHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0 || _image.PixelWidth <= 0 || _image.PixelHeight <= 0)
        {
            return;
        }

        // 小图不放大：本来就能整张看见的图片，「适应窗口」等同于实际大小
        var scale = Math.Min(viewportWidth / _image.PixelWidth, viewportHeight / _image.PixelHeight);
        _zoom = Math.Clamp(Math.Min(scale, 1.0), MinZoom, MaxZoom);
        _fitted = true;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        Preview.Width = Math.Max(1, _image.PixelWidth * _zoom);
        Preview.Height = Math.Max(1, _image.PixelHeight * _zoom);
        UpdateStatus();
    }

    private void ZoomBy(double factor)
    {
        var target = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(target - _zoom) < 0.0001) return;

        // 以视口中心为锚点缩放，指针附近的内容不会跑掉
        var centerX = (Scroller.HorizontalOffset + Scroller.ViewportWidth / 2) / Math.Max(1e-6, _zoom);
        var centerY = (Scroller.VerticalOffset + Scroller.ViewportHeight / 2) / Math.Max(1e-6, _zoom);

        _zoom = target;
        _fitted = false;
        ApplyZoom();

        Scroller.ScrollToHorizontalOffset(centerX * _zoom - Scroller.ViewportWidth / 2);
        Scroller.ScrollToVerticalOffset(centerY * _zoom - Scroller.ViewportHeight / 2);
    }

    private void OnViewerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        ZoomBy(e.Delta > 0 ? 1.15 : 1 / 1.15);
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomBy(1.25);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);

    private void OnFitClick(object sender, RoutedEventArgs e) => FitToWindow();

    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        _zoom = 1.0;
        _fitted = false;
        ApplyZoom();
    }

    // ---------------- 拖动平视 ----------------

    private void OnViewerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            if (_fitted) OnActualSizeClick(sender, e);
            else FitToWindow();
            e.Handled = true;
            return;
        }

        // 图片没超出视口时没必要捕获鼠标
        if (Scroller.ScrollableWidth <= 0 && Scroller.ScrollableHeight <= 0) return;

        _panning = true;
        _panOrigin = e.GetPosition(Scroller);
        _panOffsetX = Scroller.HorizontalOffset;
        _panOffsetY = Scroller.VerticalOffset;
        Scroller.CaptureMouse();
        Scroller.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnViewerMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;

        var current = e.GetPosition(Scroller);
        Scroller.ScrollToHorizontalOffset(_panOffsetX - (current.X - _panOrigin.X));
        Scroller.ScrollToVerticalOffset(_panOffsetY - (current.Y - _panOrigin.Y));
    }

    private void OnViewerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;

        _panning = false;
        Scroller.ReleaseMouseCapture();
        Scroller.Cursor = Cursors.Arrow;
    }

    // ---------------- 其它 ----------------

    private void UpdateStatus()
    {
        StatusText.Text = $"{_image.PixelWidth} × {_image.PixelHeight} · 缩放 {Math.Round(_zoom * 100)}%";
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var extension = ImageFormatSniffer.GuessExtension(_bytes);
            var dialog = new SaveFileDialog
            {
                Title = "图片另存为",
                FileName = Path.HasExtension(_fileName) ? _fileName : _fileName + extension,
                DefaultExt = extension,
                Filter = $"图片文件 (*{extension})|*{extension}|所有文件 (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                File.WriteAllBytes(dialog.FileName, _bytes);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存图片失败：" + ex.Message, "另存为", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
