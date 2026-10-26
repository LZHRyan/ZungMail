using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZungMail.App.ViewModels;
using ZungMail.Core.Domain;

namespace ZungMail.App.Views;

/// <summary>撰写 / 回复 / 转发窗口（FR-07 / FR-08）。</summary>
public partial class ComposeWindow : DialogWindow
{
    /// <summary>打开侧栏时的最窄宽度：再窄正文编辑区会被挤得看不见一行字。</summary>
    private const double MinWidthWithSidebar = 1040;

    private readonly ComposeViewModel _viewModel;

    /// <summary>打开侧栏前的窗口宽度，关闭侧栏时还原（用户自己拉宽过就不动）。</summary>
    private double? _widthBeforeSidebar;

    public ComposeWindow(ComposeViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // 默认按当前主窗体大小的 85% 打开；必须在 Show 之前设置，CenterOwner 才会按新尺寸居中。
        ApplyOwnerSize();

        // 上次退出时快速文本侧栏是打开的：初始宽度至少留出正文 + 侧栏的位置，
        // 否则主窗体偏小时侧栏会把正文压没
        if (viewModel.IsQuickTextPanelVisible && Width < MinWidthWithSidebar) Width = MinWidthWithSidebar;

        viewModel.CloseRequested += OnCloseRequested;
        viewModel.QuickTextInsertRequested += OnQuickTextInsertRequested;
        Loaded += async (_, _) => await viewModel.InitializeAsync();

        Editor.SendRequested += OnEditorSendRequested;
        Editor.AttachmentPickRequested += OnEditorAttachmentPickRequested;

        // 小屏幕上不让默认尺寸顶出可视区（任务栏也算进去）
        SourceInitialized += (_, _) => ClampToWorkArea();
    }

    /// <summary>按当前主窗体的实际宽高取 85%，小屏 / 小主窗体用 MinWidth/MinHeight 兜底。</summary>
    private void ApplyOwnerSize()
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is null || ReferenceEquals(owner, this)) return;

        var size = WindowSizing.OwnerRelative(
            owner.ActualWidth,
            owner.ActualHeight,
            MinWidth,
            MinHeight,
            WindowSizing.ComposeOwnerRatio);

        if (size.IsEmpty) return;
        Width = size.Width;
        Height = size.Height;
    }

    /// <summary>把窗口收进屏幕工作区：默认变大后仍保证 1366×768 这类小屏能看到完整窗口。</summary>
    private void ClampToWorkArea()
    {
        if (WindowState != WindowState.Normal) return;

        var work = SystemParameters.WorkArea;
        var maxWidth = Math.Max(MinWidth, work.Width - 60);
        var maxHeight = Math.Max(MinHeight, work.Height - 60);

        if (Width > maxWidth) Width = maxWidth;
        if (Height > maxHeight) Height = maxHeight;
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        if (result)
        {
            // 发送成功：直接关闭，不再走「保存草稿」询问。
            // 写信窗口是非模态的（Show 而非 ShowDialog），不能设 DialogResult——那样会抛异常。
            _sendCompleted = true;
            Close();
            return;
        }

        // 取消按钮：交给 OnClosing 统一决定「保存草稿 / 放弃 / 继续编辑」
        _cancelRequested = true;
        Close();
    }

    /// <summary>发送成功，关闭时不需要再问草稿。</summary>
    private bool _sendCompleted;

    /// <summary>用户点了「取消」，关闭原因只用于日志与提示文案。</summary>
    private bool _cancelRequested;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.StopAutoSave();

        if (_sendCompleted || _closingResolved || _savingDraft)
        {
            base.OnClosing(e);
            return;
        }

        // 没有任何内容：直接关，顺手清掉可能存在的旧草稿
        if (!_viewModel.HasDraftContent)
        {
            _ = _viewModel.DiscardDraftAsync();
            base.OnClosing(e);
            return;
        }

        var choice = MessageDialog.Ask(new MessageDialogOptions
        {
            Title = _cancelRequested ? "取消写邮件" : "关闭写邮件",
            Message = "这封邮件还没有发送，要保存为草稿吗？",
            Detail = "保存后会出现在「草稿箱」，双击即可继续编辑；本机还留一份用于崩溃恢复。",
            Icon = DialogIcon.Question,
            PrimaryText = "保存草稿",
            SecondaryText = "放弃",
            CancelText = "继续编辑"
        });

        if (choice == DialogChoice.Cancel)
        {
            e.Cancel = true;
            _cancelRequested = false;
            return;
        }

        if (choice == DialogChoice.Primary)
        {
            // 不在 OnClosing 里同步等落盘（会卡住 UI 线程）：先取消这次关闭，
            // 保存完再真正关窗。
            e.Cancel = true;
            _savingDraft = true;
            _ = SaveDraftThenCloseAsync();
            return;
        }

        _ = _viewModel.DiscardDraftAsync();
        _closingResolved = true;
        base.OnClosing(e);
    }

    /// <summary>正在异步保存草稿：期间忽略重复的关闭请求。</summary>
    private bool _savingDraft;

    /// <summary>
    /// 升级安装前调用：保存草稿并标记关闭已处理，随后的应用退出不会再弹「保存 / 放弃 / 继续编辑」。
    /// </summary>
    public async Task PrepareForAppShutdownAsync()
    {
        _viewModel.StopAutoSave();
        _closingResolved = true;
        if (!_viewModel.HasDraftContent) return;

        try
        {
            await _viewModel.SaveDraftAsync();
            await _viewModel.SaveDraftMailAsync();
        }
        catch (Exception)
        {
            // 草稿保存失败不阻断升级；窗口随后会被应用退出关闭
        }
    }

    private async Task SaveDraftThenCloseAsync()
    {
        try
        {
            await _viewModel.SaveDraftAsync();
            // 显式「保存草稿」才写草稿箱（自动保存只写本机 JSON，避免每 30 秒 APPEND 一次服务器）
            await _viewModel.SaveDraftMailAsync();
        }
        finally
        {
            _savingDraft = false;
            _closingResolved = true;
            Close();
        }
    }

    private bool _closingResolved;

    /// <summary>页面内 Ctrl+Enter：先冲刷编辑区内容，再走发送流程。</summary>
    private async void OnEditorSendRequested(object? sender, System.EventArgs e)
        => await SendWithFlushAsync();

    private async void OnSendClick(object sender, RoutedEventArgs e)
        => await SendWithFlushAsync();

    /// <summary>编辑器「插入附件标识 → 添加附件」：加入附件条后由绑定回推文件名列表。</summary>
    private void OnEditorAttachmentPickRequested(object? sender, IReadOnlyList<string> files)
        => _viewModel.AddAttachments(files);

    /// <summary>
    /// 页面内容回写有 260ms 防抖，发送前主动取一次完整 HTML，避免最后一次输入丢失。
    /// </summary>
    private async Task SendWithFlushAsync()
    {
        var html = await Editor.GetHtmlAsync();
        if (html is not null) _viewModel.BodyHtml = html;

        await _viewModel.SendCommand.ExecuteAsync(null);
    }

    // ---------------- 侧栏 ----------------

    private async void OnQuickTextInsertRequested(object? sender, string text)
        => await Editor.InsertQuickTextAsync(text);

    /// <summary>侧栏展开时按需加宽窗口，保证正文不被压扁；收起时还原初始宽度。</summary>
    private void OnQuickTextPanelChecked(object sender, RoutedEventArgs e)
    {
        if (Width < MinWidthWithSidebar)
        {
            _widthBeforeSidebar = Width;
            Width = MinWidthWithSidebar;
        }
    }

    private void OnQuickTextPanelUnchecked(object sender, RoutedEventArgs e)
    {
        if (_widthBeforeSidebar is { } previous && Width > previous)
        {
            Width = previous;
        }
        _widthBeforeSidebar = null;
    }

    private void OnQuickTextDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null) return;
        if (sender is not ListBox { SelectedItem: MailTemplate quick }) return;

        _viewModel.InsertQuickTextCommand.Execute(quick);
    }

    private void OnContactDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null) return;
        if (sender is not ListBox { SelectedItem: Contact contact }) return;

        _viewModel.InsertContactCommand.Execute(contact);
    }

    /// <summary>双击事件落在列表空白处时不应插入：沿可视树确认命中的是某一项。</summary>
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }
}
