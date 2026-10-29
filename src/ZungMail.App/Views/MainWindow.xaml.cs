using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ZungMail.App.Services;
using ZungMail.App.ViewModels;

namespace ZungMail.App.Views;

/// <summary>主窗口：无边框自定义标题栏 + 三栏工作区。</summary>
public partial class MainWindow : Window
{
    private Point _dragStart;
    private MailRow? _dragCandidate;

    /// <summary>
    /// 在多选行上按下鼠标时被「压住」的那一行：按下不改变选区，等拖动开始再整批拖走；
    /// 如果最终只是单击（没有拖动），鼠标抬起时再把选区收成这一行。
    /// </summary>
    private ListBoxItem? _pendingCollapse;

    private readonly MailReaderWindowService _readerWindow;

    /// <summary>搜索语法菜单正在由代码回填文本时，抑制 TextChanged 再次弹窗。</summary>
    private bool _suppressSearchSyntax;

    public MainWindow(MainViewModel viewModel, MailReaderWindowService readerWindow)
    {
        // 2614 行 XAML 的 BAML 解析 + 模板构建全在这一句里，冷机上是「窗口迟迟不出现」的主要嫌疑之一
        InitializeComponent();
        StartupTrace.Mark("主窗口 XAML 载入");

        DataContext = viewModel;
        _readerWindow = readerWindow;

        // 归因用：一页行容器 realize（模板实例化 + 首轮 Measure/Arrange）到底占多少 UI 线程
        MailList.ItemContainerGenerator.StatusChanged += OnMailContainersGenerated;

        // 自绘标题栏按钮依赖系统菜单命令，Window 不会自动注册，必须显式绑定
        WindowChromeCommands.Attach(this);

        // 无边框窗口最大化时按工作区计算尺寸，避免底部状态栏被任务栏裁掉
        WindowMaximizeBounds.Attach(this);

        // 启动大小跟随屏幕：占当前显示器工作区的 80%，居中（缩放比例由 WPF 按 DPI 自动换算）
        WindowStartupBounds.Apply(this);

        // 多选拖拽：按下时压住选区，鼠标抬起且没拖动才把选区收成一行
        MailList.PreviewMouseLeftButtonUp += OnMailListPreviewMouseLeftButtonUp;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        StartupTrace.Mark("主窗口构造完成");
    }

    /// <summary>锁定后把焦点交给密码框；展开筛选条时把焦点交给第一个输入框，方便直接 Ctrl+V。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSearchFilterOpen))
        {
            if (DataContext is not MainViewModel { IsSearchFilterOpen: true }) return;

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    SearchFilterFromBox.Focus();
                    SearchFilterFromBox.SelectAll();
                }),
                System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.IsLocked)) return;
        if (DataContext is not MainViewModel { IsLocked: true }) return;

        UnlockPasswordBox.Clear();
        Dispatcher.BeginInvoke(
            new Action(() => UnlockPasswordBox.Focus()),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e) => TryUnlock();

    private void OnUnlockPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        TryUnlock();
    }

    private void OnLockExitClick(object sender, RoutedEventArgs e) => System.Windows.Application.Current?.Shutdown();

    private void TryUnlock()
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (viewModel.TryUnlockClient(UnlockPasswordBox.Password)) return;

        UnlockPasswordBox.Clear();
        UnlockPasswordBox.Focus();
    }

    /// <summary>Ctrl+F：聚焦全局搜索框。</summary>
    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    // ---------------- 搜索语法 @ 选择器 ----------------

    /// <summary>输入 <c>@</c> / <c>＠</c> 后按已输入片段过滤语法；Esc、失焦或删掉触发符时收起。</summary>
    private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchSyntax || SearchSyntaxPopup is null || SearchSyntaxList is null) return;

        if (!SearchSyntaxCatalog.TryGetTrigger(SearchBox.Text, SearchBox.CaretIndex, out _, out var query))
        {
            CloseSearchSyntaxPicker();
            return;
        }

        var matches = SearchSyntaxCatalog.Filter(query);
        if (matches.Count == 0)
        {
            CloseSearchSyntaxPicker();
            return;
        }

        SearchSyntaxList.ItemsSource = matches;
        SearchSyntaxList.SelectedIndex = 0;
        SearchSyntaxPopup.IsOpen = true;
    }

    private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!SearchSyntaxPopup.IsOpen) return;

        switch (e.Key)
        {
            case Key.Down:
                MoveSearchSyntaxSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSearchSyntaxSelection(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSearchSyntaxSelection(5);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSearchSyntaxSelection(-5);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                AcceptSearchSyntax();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSearchSyntaxPicker();
                e.Handled = true;
                break;
        }
    }

    private void OnSearchBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!SearchSyntaxPopup.IsOpen) return;

        // 等当前鼠标/焦点事件处理完：点列表项时 Popup 仍在鼠标下，不应在 MouseUp 前关掉。
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (!SearchBox.IsKeyboardFocusWithin && !SearchSyntaxPopup.IsMouseOver)
                {
                    CloseSearchSyntaxPicker();
                }
            }),
            DispatcherPriority.Input);
    }

    private void OnSearchSyntaxMouseMove(object sender, MouseEventArgs e)
    {
        if (FindListBoxItem(e.OriginalSource as DependencyObject) is not { } item) return;
        SearchSyntaxList.SelectedItem = item.DataContext;
    }

    private void OnSearchSyntaxClick(object sender, MouseButtonEventArgs e)
    {
        if (FindListBoxItem(e.OriginalSource as DependencyObject) is not { } item) return;
        SearchSyntaxList.SelectedItem = item.DataContext;
        AcceptSearchSyntax();
        e.Handled = true;
    }

    private void MoveSearchSyntaxSelection(int delta)
    {
        var count = SearchSyntaxList.Items.Count;
        if (count == 0) return;

        var index = SearchSyntaxList.SelectedIndex;
        if (index < 0) index = 0;
        else if (delta > 0) index = (index + delta) % count;
        else index = ((index + delta) % count + count) % count;

        SearchSyntaxList.SelectedIndex = index;
        SearchSyntaxList.ScrollIntoView(SearchSyntaxList.SelectedItem);
    }

    private void AcceptSearchSyntax()
    {
        if (SearchSyntaxList.SelectedItem is not SearchSyntaxItem item) return;
        if (!SearchSyntaxCatalog.TryGetTrigger(SearchBox.Text, SearchBox.CaretIndex, out var triggerStart, out _))
        {
            CloseSearchSyntaxPicker();
            return;
        }

        var (text, caret) = SearchSyntaxCatalog.Insert(SearchBox.Text, triggerStart, SearchBox.CaretIndex, item);

        _suppressSearchSyntax = true;
        try
        {
            SearchBox.Text = text;
            SearchBox.CaretIndex = caret;
        }
        finally
        {
            _suppressSearchSyntax = false;
        }

        CloseSearchSyntaxPicker();
        SearchBox.Focus();
    }

    private void CloseSearchSyntaxPicker()
    {
        if (SearchSyntaxPopup is not null) SearchSyntaxPopup.IsOpen = false;
        if (SearchSyntaxList is not null) SearchSyntaxList.ItemsSource = null;
    }

    private ListBoxItem? FindListBoxItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListBoxItem item) return item;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>右键点击邮件行时先选中该行，保证上下文菜单命令作用于目标邮件。</summary>
    private void OnMailItemRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item) return;

        item.IsSelected = true;
        item.Focus();
    }

    /// <summary>邮件操作员快捷键；文字输入和富文本编辑焦点中只保留其原生编辑行为。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // 锁定状态下屏蔽窗口级快捷键（Ctrl+N/R/F、标签跳转等），把输入留给解锁密码框。
        if (vm.IsLocked && !UnlockPasswordBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            UnlockPasswordBox.Focus();
            return;
        }

        if (e.Key == Key.Escape && SearchSyntaxPopup.IsOpen)
        {
            // 先把 @ 语法菜单收起，保留用户已输入的搜索词；再按一次 Esc 才清空搜索。
            CloseSearchSyntaxPicker();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (vm.SearchText.Length > 0 && vm.ClearSearchCommand.CanExecute(null))
            {
                vm.ClearSearchCommand.Execute(null);
            }
            return;
        }

        if (IsTextInputFocused()) return;

        // 标签快捷键：单键（1-9 / A-Z / F1-F12，不带修饰键）→ 给选中的邮件打 / 取消该标签。
        // 只有当前账户确实绑定了这个键才拦截；带修饰键的组合键（Ctrl+N 等）照常走原有快捷键。
        var hotkey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.None
            && TagHotkeys.TryGetToken(hotkey, out var tagKey)
            && vm.HasTagShortcut(tagKey))
        {
            _ = vm.ToggleTagByShortcutAsync(tagKey);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Oem2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            SearchBox.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && vm.DeleteMessageCommand.CanExecute(null))
        {
            vm.DeleteMessageCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            MailList.SelectAll();
            vm.SelectCurrentPageCommand.Execute(null);
            e.Handled = true;
            return;
        }

        var isShiftNavigation = Keyboard.Modifiers == ModifierKeys.Shift && e.Key is Key.J or Key.K or Key.I;
        if (Keyboard.Modifiers != ModifierKeys.None && !isShiftNavigation) return;

        switch (e.Key)
        {
            case Key.J:
                vm.SelectRelative(1, Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift));
                ScrollSelectedIntoView(vm);
                break;
            case Key.K:
                vm.SelectRelative(-1, Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift));
                ScrollSelectedIntoView(vm);
                break;
            case Key.R when vm.ReplyCommand.CanExecute(null): vm.ReplyCommand.Execute(null); break;
            case Key.A when vm.ReplyAllCommand.CanExecute(null): vm.ReplyAllCommand.Execute(null); break;
            case Key.F when vm.ForwardCommand.CanExecute(null): vm.ForwardCommand.Execute(null); break;
            case Key.E when vm.ArchiveCommand.CanExecute(null): vm.ArchiveCommand.Execute(null); break;
            case Key.S when vm.ToggleStarCommand.CanExecute(null): vm.ToggleStarCommand.Execute(null); break;
            case Key.U when vm.MarkSelectedUnreadCommand.CanExecute(null): vm.MarkSelectedUnreadCommand.Execute(null); break;
            case Key.I when isShiftNavigation && vm.MarkSelectedReadCommand.CanExecute(null): vm.MarkSelectedReadCommand.Execute(null); break;
            case Key.X when vm.ToggleBatchSelectionCommand.CanExecute(null): vm.ToggleBatchSelectionCommand.Execute(null); break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnMailSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.UpdateBatchSelection(MailList.SelectedItems.OfType<MailRow>());
        }
    }

    /// <summary>行容器 realize 期间的 UI 线程占用（归因用；切页/刷新时各记一次）。</summary>
    private IDisposable? _containerRealizeScope;

    /// <summary>
    /// 容器生成完毕后开始计时，等这一轮布局结束（Loaded 优先级回到队列尾部）再收口。
    /// WPF 的模板实例化 + 首轮 Measure/Arrange 都发生在容器生成之后的同一次布局里，
    /// 因此这个窗口正好覆盖「一页 100 行到底要多久」。
    /// </summary>
    private void OnMailContainersGenerated(object? sender, EventArgs e)
    {
        if (MailList.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated) return;

        _containerRealizeScope?.Dispose();
        _containerRealizeScope = UiThreadProbe.Measure("行容器 realize");

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _containerRealizeScope?.Dispose();
                _containerRealizeScope = null;
            }),
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 双击邮件行：草稿箱里的草稿用写信窗口打开继续编辑，其余在独立阅读窗口打开
    /// （整个客户端只保留一个阅读窗口，焦点回到列表时仍可继续切换）。
    /// 双击滚动条或空白区不命中任何行，保持原行为。
    /// </summary>
    private void OnMailListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var container = ItemsControl.ContainerFromElement(MailList, e.OriginalSource as DependencyObject);
        if (container is not ListBoxItem { DataContext: MailRow row }) return;

        if (!ReferenceEquals(vm.SelectedRow, row)) vm.SelectedRow = row;

        if (row.Model.IsDraftFlag)
            _ = vm.OpenDraftAsync(row.Id);
        else
            _readerWindow.Show(row.Id);

        e.Handled = true;
    }

    /// <summary>点击头部备注条：展开右侧上下文栏并把焦点交给备注输入框。</summary>
    private void OnNoteRowClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (!vm.IsContextSidebarVisible && vm.ToggleContextSidebarCommand.CanExecute(null))
        {
            vm.ToggleContextSidebarCommand.Execute(null);
        }

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                NoteBox.Focus();
                NoteBox.CaretIndex = NoteBox.Text.Length;
            }),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>单击发件人地址：弹出联系人卡片（写邮件 / 相关邮件 / 复制地址）。</summary>
    private void OnContactCardClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.ToggleContactCardCommand.CanExecute(null)) vm.ToggleContactCardCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>单击「收件人：」整行：展开收件人列表卡片。</summary>
    private void OnRecipientRowClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.ToggleRecipientCardCommand.CanExecute(null))
        {
            vm.ToggleRecipientCardCommand.Execute(null);
        }
        e.Handled = true;
    }

    /// <summary>左键单击某个地址：打开/收起该地址的联系人卡片。</summary>
    private void OnAddressChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MailAddressItem item })
        {
            item.IsCardOpen = !item.IsCardOpen;
            e.Handled = true;
        }
    }

    /// <summary>「+」按钮：左键直接弹出「显示哪些搜索条件」的勾选菜单（和 Foxmail 的 +▾ 一致）。</summary>
    private void OnSearchFilterFieldsMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } element) return;

        menu.PlacementTarget = element;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

        // 真实鼠标点击时若同步 IsOpen，菜单会被紧随其后的鼠标抬起/捕获切换立刻关掉（表现为点了没反应），
        // 延后到本轮输入事件处理完再打开就稳定了。
        Dispatcher.BeginInvoke(new Action(() => menu.IsOpen = true), DispatcherPriority.Input);
        e.Handled = true;
    }

    private void ScrollSelectedIntoView(MainViewModel vm)
    {
        if (vm.SelectedRow is not null) MailList.ScrollIntoView(vm.SelectedRow);
    }

    // ---------------- 邮件拖拽 ----------------

    private void OnMailItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not MailRow row) return;

        _dragStart = e.GetPosition(MailList);
        _dragCandidate = row;
        _pendingCollapse = null;

        // 多选之后再按其中一行（不按 Ctrl / Shift）时，WPF 会先把选区收成单选，
        // 于是拖拽只能带走这一封。这里压住这次按下：选区保持不动，拖动时整批拖走；
        // 如果抬起鼠标前没拖动，再按普通单击处理（收成这一行）。
        var hasModifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        if (!MailDragSelection.ShouldPreserveSelection(item.IsSelected, MailList.SelectedItems.Count, hasModifier))
        {
            return;
        }

        _pendingCollapse = item;
        // 自己接管鼠标捕获，拖动过程中的 MouseMove 才会继续送到这一行
        item.Focus();
        item.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>没拖动就松手：把多选收成按下的那一行（等同普通单击）。</summary>
    private void OnMailListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = _pendingCollapse;
        _pendingCollapse = null;
        _dragCandidate = null;

        if (item is null) return;

        item.ReleaseMouseCapture();
        if (DataContext is not MainViewModel vm || item.DataContext is not MailRow row) return;

        // 只移除其它行，避免先清空再选回造成详情区闪两次
        foreach (var other in MailList.SelectedItems.OfType<MailRow>().Where(selected => !ReferenceEquals(selected, row)).ToList())
        {
            MailList.SelectedItems.Remove(other);
        }

        item.IsSelected = true;
        vm.SelectedRow = row;
        e.Handled = true;
    }

    private void OnMailItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;

        var current = e.GetPosition(MailList);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // 若当前行在多选集合中，拖动整批；否则仅拖动当前行。
        var ids = MailList.SelectedItems
            .OfType<MailRow>()
            .Select(row => row.Id)
            .ToList();
        if (!ids.Contains(_dragCandidate.Id)) ids = new List<long> { _dragCandidate.Id };

        _dragCandidate = null;

        // 真的开始拖了：不要再在抬鼠标时把选区收成一行
        if (_pendingCollapse is { } pressed)
        {
            _pendingCollapse = null;
            pressed.ReleaseMouseCapture();
        }

        var payload = new MailDragPayload(ids);
        DragDrop.DoDragDrop(MailList, new DataObject(typeof(MailDragPayload), payload), DragDropEffects.Move);
    }

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        var accepted = e.Data.GetDataPresent(typeof(MailDragPayload))
            && sender is FrameworkElement { DataContext: FolderNode { CanReceiveMailDrop: true } };

        e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;

        if (sender is Grid grid && grid.FindName("DropHighlight") is Border highlight)
        {
            highlight.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnFolderDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Grid grid && grid.FindName("DropHighlight") is Border highlight)
        {
            highlight.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        if (sender is not Grid grid) return;

        if (grid.FindName("DropHighlight") is Border highlight) highlight.Visibility = Visibility.Collapsed;
        if (grid.DataContext is not FolderNode folder || !folder.CanReceiveMailDrop) return;
        if (e.Data.GetData(typeof(MailDragPayload)) is not MailDragPayload payload) return;

        e.Handled = true;
        if (DataContext is MainViewModel vm)
        {
            await vm.MoveDroppedMessagesAsync(payload.MessageIds, folder);
        }
    }

    /// <summary>打印当前邮件正文：交给 WebView2 原生打印，保留 HTML 排版。</summary>
    private void OnPrintMailClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // 纯文本降级模式下无法原样打印，提示用户切换到 HTML 渲染
        if (!vm.UseHtmlRendering)
        {
            vm.StatusText = "当前为纯文本显示，切换到 HTML 渲染后可打印完整排版";
            return;
        }

        if (!BodyView.Print())
        {
            vm.StatusText = "正文尚未加载完成，稍后再试打印";
        }
    }
    /// <summary>输入/可选中控件内的按键应交给文本编辑，不触发邮件快捷操作。</summary>
    private static bool IsTextInputFocused()
        => Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or RichTextBox;

    /// <summary>标题栏拖动与双击最大化（按钮 / 输入框会自行处理事件，不会冒泡到此处）。</summary>
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标按键已在拖动前释放，忽略
        }
    }
}
