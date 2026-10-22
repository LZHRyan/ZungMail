using System;
using System.Windows;
using System.Windows.Input;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Views;

/// <summary>
/// 独立阅读窗口：双击邮件列表打开，客户端内只保留一个实例（双击另一封时切换内容）。
/// 外壳沿用 <see cref="DialogWindow"/> 的无边框与自绘标题栏，但以非模态方式显示，
/// 关闭主窗口的其它操作不会被它挡住。
/// </summary>
public partial class MailReaderWindow : DialogWindow
{
    public MailReaderWindow(MailReaderViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 默认占主窗体当前大小的 85%（和写信窗口一致），双击打开就能看清正文
        ApplyOwnerSize();

        // 小屏幕上不让默认尺寸顶出工作区
        SourceInitialized += (_, _) => ClampToWorkArea();
    }

    /// <summary>按当前主窗体的实际宽高取 85%，小屏 / 小主窗体用 MinWidth / MinHeight 兜底。</summary>
    private void ApplyOwnerSize()
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is null || ReferenceEquals(owner, this)) return;

        var size = WindowSizing.OwnerRelative(
            owner.ActualWidth,
            owner.ActualHeight,
            MinWidth,
            MinHeight,
            WindowSizing.ReaderOwnerRatio);

        if (size.IsEmpty) return;
        Width = size.Width;
        Height = size.Height;
    }

    /// <summary>把窗口收进屏幕工作区：主窗体很大时也不让阅读窗口顶到任务栏外。</summary>
    private void ClampToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        var maxWidth = Math.Max(MinWidth, work.Width - 60);
        var maxHeight = Math.Max(MinHeight, work.Height - 60);

        if (Width > maxWidth) Width = maxWidth;
        if (Height > maxHeight) Height = maxHeight;
    }

    /// <summary>标题栏拖动 / 双击最大化：沿用对话框外壳的窗口行为。</summary>
    private void OnReaderTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        => OnTitleBarMouseDown(sender, e);

    /// <summary>Esc 关闭阅读窗口（正文获得键盘焦点时由 WebView2 自己处理）。</summary>
    private void OnReaderPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        Close();
    }
}
