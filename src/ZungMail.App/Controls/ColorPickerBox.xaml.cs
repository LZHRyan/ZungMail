using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZungMail.App.Controls;

/// <summary>
/// 可复用颜色选择组件：左侧色块 + 十六进制输入 + 系统拾色器按钮。
/// 通过 <see cref="Hex"/> 双向绑定颜色，任何需要选色的地方都可直接引用。
/// </summary>
public partial class ColorPickerBox : UserControl
{
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorPickerBox),
        new FrameworkPropertyMetadata("#2563eb", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHexChanged));

    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    public ColorPickerBox()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateSwatch();
    }

    private static void OnHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorPickerBox picker && picker.IsLoaded)
        {
            picker.UpdateSwatch();
        }
    }

    private void UpdateSwatch()
    {
        try
        {
            Swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Hex ?? ""));
        }
        catch (Exception)
        {
            Swatch.Background = Brushes.Transparent;
        }
    }

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = ToDrawingColor(Hex)
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        Hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        UpdateSwatch();
    }

    private static System.Drawing.Color ToDrawingColor(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex ?? "");
            return System.Drawing.Color.FromArgb(color.R, color.G, color.B);
        }
        catch (Exception)
        {
            return System.Drawing.Color.FromArgb(37, 99, 235);
        }
    }
}
