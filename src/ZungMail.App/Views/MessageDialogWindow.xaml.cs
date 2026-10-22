using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ZungMail.App.Views;

/// <summary>
/// 自绘信息 / 确认对话框：统一 46px 标题栏、色块图标、圆角按钮与灰底说明块，
/// 用来替代外观孤立的系统 <c>MessageBox</c>。
/// <para>
/// 交互约定与系统对话框保持一致：Enter 触发主操作，Esc / 右上角关闭 = 取消。
/// </para>
/// </summary>
public partial class MessageDialogWindow : DialogWindow
{
    private DialogChoice _result = DialogChoice.Cancel;

    internal MessageDialogWindow() => InitializeComponent();

    /// <summary>用户选择结果（窗口关闭后读取）。</summary>
    internal DialogChoice Result => _result;

    /// <summary>把参数套到界面元素上；所有值在 ShowDialog 之前设置。</summary>
    internal void Apply(MessageDialogOptions options)
    {
        HeaderText.Text = options.Title;
        MessageText.Text = options.Message;

        if (string.IsNullOrWhiteSpace(options.Detail))
        {
            DetailHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailText.Text = options.Detail;
            DetailHost.Visibility = Visibility.Visible;
        }

        ApplyIcon(options.Icon);

        PrimaryButton.Content = options.PrimaryText;
        if (options.Danger) PrimaryButton.Style = (Style)FindResource("BtnDanger");

        // 破坏性操作（删除 / 彻底清理）不给 Enter 默认确认，避免误按回车就执行，
        // 只能显式点击或用 Tab 移到按钮上确认；Esc 始终等于取消。
        PrimaryButton.IsDefault = !options.Danger;

        SecondaryButton.Content = options.SecondaryText;
        SecondaryButton.Visibility = string.IsNullOrWhiteSpace(options.SecondaryText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        CancelButton.Content = options.CancelText;
        CancelButton.Visibility = string.IsNullOrWhiteSpace(options.CancelText)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // 焦点留在窗口上：Enter 由主按钮的 IsDefault 处理，Esc 由 PreviewKeyDown 处理，
        // 不给按钮加可见焦点环
        Focus();
    }

    private void ApplyIcon(DialogIcon icon)
    {
        if (icon == DialogIcon.None)
        {
            IconHost.Visibility = Visibility.Collapsed;
            MessageStack.Margin = new Thickness(0);
            return;
        }

        var (geometry, strokeKey, backgroundKey) = icon switch
        {
            DialogIcon.Success => ("IcoCheck", "Accent2Brush", "Accent2SoftBrush"),
            DialogIcon.Warning => ("IcoAlert", "WarningBrush", "WarningSoftBrush"),
            DialogIcon.Error => ("IcoError", "CriticalBrush", "CriticalSoftBrush"),
            DialogIcon.Question => ("IcoQuestion", "AccentBrush", "AccentSoftBrush"),
            _ => ("IcoInfo", "AccentBrush", "AccentSoftBrush")
        };

        IconGlyph.Data = (Geometry)FindResource(geometry);
        IconGlyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, strokeKey);
        IconHost.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, backgroundKey);
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => Finish(DialogChoice.Primary);

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => Finish(DialogChoice.Secondary);

    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(DialogChoice.Cancel);

    private void OnDismissClick(object sender, RoutedEventArgs e) => Finish(DialogChoice.Cancel);

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        Finish(DialogChoice.Cancel);
    }

    private void Finish(DialogChoice choice)
    {
        _result = choice;
        Close();
    }
}
