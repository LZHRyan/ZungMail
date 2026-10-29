using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZungMail.App.Services;
using ZungMail.Core.Services;
using ZungMail.Infrastructure.Logging;
using Microsoft.Web.WebView2.Core;

namespace ZungMail.App.Views;

/// <summary>
/// 邮件正文承载控件：优先用 WebView2 渲染已消毒的 HTML 文档；
/// 运行时缺失、渲染进程崩溃或用户选择纯文本时，在同一位面降级为只读富文本
/// （纯文本段落 + 正文内嵌图片）。
/// </summary>
public partial class MailBodyView : UserControl
{
    private const string VirtualHost = HtmlMailDocumentBuilder.DefaultVirtualHost;

    /// <summary>大正文写文件后导航用的虚拟主机（映射到 <see cref="MapBodyFolder"/> 建的目录）。</summary>
    private const string BodyVirtualHost = "zungmailbody.example";

    private CoreWebView2Environment? _environment;
    private bool _initialized;
    private bool _failed;

    /// <summary>大正文落盘的目录（<see cref="BodyVirtualHost"/> 映射的本地路径）；映射失败时为 null。</summary>
    private string? _bodyDirectory;

    /// <summary>本实例复用的正文文件：每封邮件覆盖写，用一个递增查询串绕开浏览器缓存。</summary>
    private string? _bodyFilePath;
    private int _bodyFileVersion;

    /// <summary>WebView2 已释放（宿主窗口关闭后），禁止再初始化或访问内核。</summary>
    private bool _browserReleased;

    /// <summary>宿主窗口：关闭时释放 WebView2，否则渲染进程会一直挂在进程树里。</summary>
    private Window? _ownerWindow;

    /// <summary>NavigateToString 会以 data:text/html 触发导航；按待处理计数放行自身导航。</summary>
    private int _pendingSelfNavigations;

    /// <summary>大正文「异步写盘 → 导航」的版本号：写完时若已换邮件就丢弃这次结果。</summary>
    private int _renderVersion;

    /// <summary>降级文档重建已排队，避免正文与图片列表连续变更时重复解码。</summary>
    private bool _plainDocumentQueued;

    /// <summary>「图片双击已启用」只记一次日志，免得每封邮件刷一行。</summary>
    private static bool _imageScriptReadyLogged;

    public MailBodyView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // ---------------- 依赖属性 ----------------

    /// <summary>是否使用 HTML 渲染；false 时直接显示 <see cref="PlainText"/>。</summary>
    public static readonly DependencyProperty UseHtmlProperty = DependencyProperty.Register(
        nameof(UseHtml), typeof(bool), typeof(MailBodyView),
        new PropertyMetadata(true, OnUseHtmlChanged));

    public bool UseHtml
    {
        get => (bool)GetValue(UseHtmlProperty);
        set => SetValue(UseHtmlProperty, value);
    }

    /// <summary>完整 HTML 文档（已含 CSP / 主题样式 / URI 重写）。</summary>
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(string), typeof(MailBodyView),
        new PropertyMetadata(null, OnDocumentChanged));

    public string? Document
    {
        get => (string?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>纯文本回退内容。</summary>
    public static readonly DependencyProperty PlainTextProperty = DependencyProperty.Register(
        nameof(PlainText), typeof(string), typeof(MailBodyView),
        new PropertyMetadata("", OnPlainContentChanged));

    public string PlainText
    {
        get => (string)GetValue(PlainTextProperty);
        set => SetValue(PlainTextProperty, value);
    }

    /// <summary>原始正文 HTML（含 file:/// 内嵌图片引用），供降级位面转换为富文本。</summary>
    public static readonly DependencyProperty HtmlSourceProperty = DependencyProperty.Register(
        nameof(HtmlSource), typeof(string), typeof(MailBodyView),
        new PropertyMetadata("", OnPlainContentChanged));

    public string HtmlSource
    {
        get => (string)GetValue(HtmlSourceProperty);
        set => SetValue(HtmlSourceProperty, value);
    }

    /// <summary>降级位面可内联展示的图片路径（正文内嵌资源）。</summary>
    public static readonly DependencyProperty InlineImagePathsProperty = DependencyProperty.Register(
        nameof(InlineImagePaths), typeof(IReadOnlyList<string>), typeof(MailBodyView),
        new PropertyMetadata(null, OnPlainContentChanged));

    public IReadOnlyList<string>? InlineImagePaths
    {
        get => (IReadOnlyList<string>?)GetValue(InlineImagePathsProperty);
        set => SetValue(InlineImagePathsProperty, value);
    }

    /// <summary>正文字号（与阅读窗格设置联动）。</summary>
    public static readonly DependencyProperty BodyFontSizeProperty = DependencyProperty.Register(
        nameof(BodyFontSize), typeof(double), typeof(MailBodyView),
        new PropertyMetadata(13.5, OnBodyFontSizeChanged));

    public double BodyFontSize
    {
        get => (double)GetValue(BodyFontSizeProperty);
        set => SetValue(BodyFontSizeProperty, value);
    }

    /// <summary>内联附件所在目录；映射为虚拟主机以允许文档内加载本地图片。</summary>
    public static readonly DependencyProperty EmlDirectoryProperty = DependencyProperty.Register(
        nameof(EmlDirectory), typeof(string), typeof(MailBodyView), new PropertyMetadata(null));

    public string? EmlDirectory
    {
        get => (string?)GetValue(EmlDirectoryProperty);
        set => SetValue(EmlDirectoryProperty, value);
    }

    /// <summary>当前主题，用于设置 WebView2 默认底色，避免暗色下的闪白。</summary>
    public static readonly DependencyProperty IsDarkProperty = DependencyProperty.Register(
        nameof(IsDark), typeof(bool), typeof(MailBodyView),
        new PropertyMetadata(false, OnIsDarkChanged));

    public bool IsDark
    {
        get => (bool)GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    // ---------------- 生命周期 ----------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachOwnerWindow();

        if (_browserReleased) return;

        if (_initialized || _failed)
        {
            UpdateMode();
            return;
        }

        if (!UseHtml)
        {
            UpdateMode();
            return;
        }

        // 设置页里的模板预览整块是 Collapsed：等真正显示再建浏览器，
        // 否则每开一次设置就多一份渲染进程（隐藏期间仍可看纯文本降级位面）。
        if (!IsVisible)
        {
            IsVisibleChanged += OnVisibleChanged;
            UpdateMode();
            return;
        }

        await EnsureCoreAsync();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_browserReleased) return;

        if (!IsVisible)
        {
            UpdateMode();
            return;
        }

        IsVisibleChanged -= OnVisibleChanged;
        if (_initialized || _failed || !UseHtml) return;
        _ = EnsureCoreAsync();
    }

    private void AttachOwnerWindow()
    {
        if (_ownerWindow is not null) return;

        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null) _ownerWindow.Closed += OnOwnerWindowClosed;
    }

    private void OnOwnerWindowClosed(object? sender, EventArgs e) => ReleaseBrowser();

    /// <summary>
    /// 释放 WebView2 与内核事件订阅。WPF 的 WebView2 实现了 IDisposable，
    /// 不显式释放时渲染进程会留在进程树里直到进程退出（实测每次开窗多一份）。
    /// </summary>
    private void ReleaseBrowser()
    {
        if (_browserReleased) return;
        _browserReleased = true;
        _initialized = false;

        IsVisibleChanged -= OnVisibleChanged;

        if (_ownerWindow is not null)
        {
            _ownerWindow.Closed -= OnOwnerWindowClosed;
            _ownerWindow = null;
        }

        try
        {
            if (Browser.CoreWebView2 is { } core)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.ContextMenuRequested -= OnContextMenuRequested;
                core.ProcessFailed -= OnProcessFailed;
            }
        }
        catch (Exception)
        {
            // 内核未初始化或已崩溃：没有可退订的订阅
        }

        try { Browser.Dispose(); }
        catch (Exception)
        {
            // 释放失败交给进程退出兜底，不影响窗口关闭
        }

        DeleteBodyFile();
    }

    private async Task EnsureCoreAsync()
    {
        try
        {
            if (!WebViewRuntimeProbe.IsAvailable)
            {
                Fail();
                return;
            }

            var userDataFolder = WebViewRuntimeProbe.GetUserDataFolder();
            Directory.CreateDirectory(userDataFolder);

            _environment = await WebViewEnvironmentCache.GetAsync(userDataFolder);
            if (_browserReleased) return;

            await Browser.EnsureCoreWebView2Async(_environment);
            if (_browserReleased) return;

            var core = Browser.CoreWebView2;
            ApplySecuritySettings(core);

            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NewWindowRequested += OnNewWindowRequested;
            core.ContextMenuRequested += OnContextMenuRequested;
            core.ProcessFailed += OnProcessFailed;

            var eml = EmlDirectory;
            if (!string.IsNullOrWhiteSpace(eml) && Directory.Exists(eml))
            {
                core.SetVirtualHostNameToFolderMapping(
                    VirtualHost, eml, CoreWebView2HostResourceAccessKind.DenyCors);
            }

            MapBodyFolder(core, userDataFolder);

            _initialized = true;
            UpdateMode();
        }
        catch (Exception)
        {
            Fail();
        }
    }

    /// <summary>
    /// 把「组装好的正文文档」目录映射成虚拟主机。
    ///
    /// <c>NavigateToString</c> 的实参上限是 2MB（WebView2 官方文档），8MB 的邮件必然抛异常；
    /// 大正文改成写文件 + <c>Navigate</c>，解析排版交给渲染进程，界面线程只花几毫秒写盘。
    /// </summary>
    private void MapBodyFolder(CoreWebView2 core, string userDataFolder)
    {
        try
        {
            var directory = Path.Combine(userDataFolder, "body");
            Directory.CreateDirectory(directory);
            SweepStaleBodyFiles(directory);

            core.SetVirtualHostNameToFolderMapping(
                BodyVirtualHost, directory, CoreWebView2HostResourceAccessKind.DenyCors);

            _bodyDirectory = directory;
        }
        catch (Exception ex)
        {
            // 映射失败不致命：大正文退回纯文本（小正文照旧走 NavigateToString）
            _bodyDirectory = null;
            AppLog.Warn("正文目录映射失败，大正文将显示为纯文本：" + ex.Message);
        }
    }

    /// <summary>清掉上次异常退出留下的正文文件（只删一天前的，避免误删其它实例正在用的）。</summary>
    private static void SweepStaleBodyFiles(string directory)
    {
        try
        {
            var limit = DateTime.UtcNow.AddDays(-1);
            foreach (var file in Directory.EnumerateFiles(directory, "body-*.html"))
            {
                if (File.GetLastWriteTimeUtc(file) < limit) File.Delete(file);
            }
        }
        catch (Exception)
        {
            // 清理失败只影响临时文件体积，不影响阅读
        }
    }

    private void DeleteBodyFile()
    {
        if (_bodyFilePath is null) return;

        try
        {
            File.Delete(_bodyFilePath);
        }
        catch (Exception)
        {
            // 文件被占用：留给下次启动的过期清理
        }

        _bodyFilePath = null;
    }

    /// <summary>打印当前正文：调用 WebView2 原生打印对话框，保持与阅读时一致的排版。</summary>
    public bool Print()
    {
        try
        {
            if (Browser.CoreWebView2 is not { } core) return false;
            core.ShowPrintUI(CoreWebView2PrintDialogKind.System);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 九项安全设置：邮件正文视为不可信内容，除排版与选中复制外一律关闭。
    /// 右键菜单保持「可请求」状态，但宿主在 <see cref="OnContextMenuRequested"/> 中全部接管，
    /// 用户只会看到自建的「复制 / 全选」。
    ///
    /// 脚本仍然关闭（邮件自带的 &lt;script&gt; 与 on* 属性执行不了）；只打开「页面 → 宿主」的消息通道，
    /// 供宿主注入的双击脚本回传被双击的图片地址，见 <see cref="MailBodyImageScript"/>。
    /// </summary>
    private static void ApplySecuritySettings(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = true;
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
    }

    private void UpdateMode()
    {
        if (_browserReleased) return;

        var html = UseHtml && !_failed && _initialized;
        Browser.Visibility = html ? Visibility.Visible : Visibility.Collapsed;
        PlainTextHost.Visibility = html ? Visibility.Collapsed : Visibility.Visible;

        if (html) Render();
        else BuildPlainDocument();
    }

    /// <summary>纯文本与图片列表可能连续变更，合并为一次重建，避免重复解码图片。</summary>
    private void SchedulePlainDocument()
    {
        if (_plainDocumentQueued) return;

        _plainDocumentQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _plainDocumentQueued = false;
            if (!(UseHtml && !_failed && _initialized)) BuildPlainDocument();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>构建降级位面的只读文档：优先把 HTML 转成富文本，失败时退化为纯文本 + 末尾图片。</summary>
    private void BuildPlainDocument()
    {
        var html = HtmlSource;

        // 几 MB 的正文交给 WPF 富文本要一两秒（FlowDocument 逐元素构建，实测 6.8MB ≈ 1.7–3.1s），
        // 而且会被重建两次（渲染失败一次、正文属性变更排队又一次）——这里按体积止损，直接排纯文本。
        var richAllowed = MailBodyRenderPolicy.AllowRichFallback(html);

        if (richAllowed && HtmlFlowDocumentBuilder.TryBuild(html, IsDark, BodyFontSize, out var htmlDocument))
        {
            PlainTextHost.Document = htmlDocument;
            return;
        }

        var document = new FlowDocument { PagePadding = new Thickness(0) };

        if (!richAllowed)
        {
            document.Blocks.Add(new Paragraph(new Run(MailBodyRenderPolicy.LargeBodyFallbackNotice))
            {
                Margin = new Thickness(0, 0, 0, 10)
            });
        }

        var lines = (PlainText ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        foreach (var line in lines)
        {
            document.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0) });
        }

        foreach (var path in InlineImagePaths ?? Array.Empty<string>())
        {
            var image = TryLoadImage(path);
            if (image is null) continue;
            document.Blocks.Add(new BlockUIContainer(image) { Margin = new Thickness(0, 12, 0, 0) });
        }

        PlainTextHost.Document = document;
    }

    /// <summary>按需解码本地图片；文件缺失或格式不支持时跳过，不影响文本阅读。</summary>
    private static Image? TryLoadImage(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            return new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = 640,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Render()
    {
        var document = Document;
        if (string.IsNullOrEmpty(document)) return;

        try
        {
            ApplyBackgroundColor();

            using var scope = UiThreadProbe.Measure("正文渲染");

            // 超过 NavigateToString 的 2MB 上限：写文件后按 URL 导航，避免每次都抛异常再退回纯文本
            if (_bodyDirectory is not null && MailBodyRenderPolicy.NeedsFileNavigation(document))
            {
                // 几 MB 的正文同步写盘会让这一帧明显卡顿；写文件放线程池，写完再用版本号确认没换邮件
                var version = ++_renderVersion;
                var write = WriteBodyFileAsync(document);
                _ = FinishFileNavigationAsync(write, version);
                return;
            }

            _pendingSelfNavigations++;
            Browser.CoreWebView2.NavigateToString(document);
        }
        catch (Exception ex)
        {
            if (_pendingSelfNavigations > 0) _pendingSelfNavigations--;
            // 以前这里静默吞掉：大邮件每次都失败，日志里却一条记录都没有
            Fail(ex);
        }
    }

    /// <summary>等大正文写完再导航；期间若已切换到别的邮件（版本号变了）就丢弃这次结果。</summary>
    private async Task FinishFileNavigationAsync(Task<string> write, int version)
    {
        try
        {
            var url = await write.ConfigureAwait(true);
            if (version != _renderVersion || _failed) return;

            _pendingSelfNavigations++;
            Browser.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// 把组装好的文档写成文件并返回虚拟主机地址（写盘在线程池执行）。
    /// 同一个控件复用一份文件（每封邮件覆盖写），浏览器缓存用递增查询串绕开，避免临时文件堆积。
    /// </summary>
    private async Task<string> WriteBodyFileAsync(string document)
    {
        _bodyFilePath ??= Path.Combine(_bodyDirectory!, "body-" + Guid.NewGuid().ToString("N") + ".html");
        var path = _bodyFilePath;

        await Task.Run(() => File.WriteAllText(path, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            .ConfigureAwait(true);

        _bodyFileVersion++;
        return $"https://{BodyVirtualHost}/{Path.GetFileName(path)}?v={_bodyFileVersion}";
    }

    private void ApplyBackgroundColor()
        => Browser.DefaultBackgroundColor = IsDark
            ? System.Drawing.Color.FromArgb(0x16, 0x1B, 0x23)
            : System.Drawing.Color.White;

    private void Fail(Exception? error = null)
    {
        if (_browserReleased) return;

        if (error is not null)
        {
            // 大邮件曾被这条静默吞掉的异常一路带到降级位面（NavigateToString 的 2MB 上限），
            // 出问题时至少要能在日志里看到原因
            AppLog.Warn($"正文 HTML 渲染失败，已退回纯文本：{error.GetType().Name}: {error.Message}");
        }

        _failed = true;
        _pendingSelfNavigations = 0;
        Browser.Visibility = Visibility.Collapsed;
        PlainTextHost.Visibility = Visibility.Visible;
        BuildPlainDocument();
    }

    // ---------------- 事件 ----------------

    private static void OnUseHtmlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MailBodyView)d;

        // 运行中从纯文本切回 HTML：补一次内核初始化
        if (view.UseHtml && view.IsLoaded && !view._initialized && !view._failed)
        {
            _ = view.EnsureCoreAsync();
            return;
        }

        view.UpdateMode();
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MailBodyView)d;
        if (view._initialized && !view._failed && view.UseHtml) view.Render();
    }

    private static void OnPlainContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MailBodyView)d).SchedulePlainDocument();

    private static void OnBodyFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MailBodyView)d).SchedulePlainDocument();

    /// <summary>主题切换时调整 WebView2 底色，并重建降级文档（暗色下不套用邮件自带配色）。</summary>
    private static void OnIsDarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MailBodyView)d;
        if (view._initialized) view.ApplyBackgroundColor();
        view.SchedulePlainDocument();
    }

    /// <summary>只允许文档自身与虚拟主机资源；外部 http(s) 链接交给系统浏览器。</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Uri)) return;

        // 自身文档（NavigateToString 会以 data:text/html 形式导航）
        if (e.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
        {
            if (_pendingSelfNavigations > 0)
            {
                _pendingSelfNavigations--;
                return;
            }

            e.Cancel = true;
            return;
        }

        // 文档内锚点跳转
        if (e.Uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)) return;

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            // 大正文的本地文件导航（见 MapBodyFolder）：算自身导航，放行并销账
            if (uri.Scheme == Uri.UriSchemeHttps &&
                string.Equals(uri.Host, BodyVirtualHost, StringComparison.OrdinalIgnoreCase))
            {
                if (_pendingSelfNavigations > 0) _pendingSelfNavigations--;
                return;
            }

            if (uri.Scheme == Uri.UriSchemeHttps &&
                string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            e.Cancel = true;

            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                OpenExternal(uri);
            }

            return;
        }

        e.Cancel = true;
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            OpenExternal(uri);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e) => Fail();

    /// <summary>
    /// 导航完成（<c>NavigateToString</c> 与正文文件导航都算）后注入「双击图片」脚本。
    /// 脚本本身不含邮件内容，只把被双击的图片地址回传给宿主，所以每次导航重新注入即可。
    /// </summary>
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _browserReleased) return;

        try
        {
            var core = Browser.CoreWebView2;
            if (core is null) return;

            await core.ExecuteScriptAsync(MailBodyImageScript.Inject);
            if (_browserReleased) return;

            // 只在没生效时说话：脚本被内核拒绝（例如脚本整体禁用）会在这里暴露
            var applied = await core.ExecuteScriptAsync("window." + MailBodyImageScript.Marker);
            if (!string.Equals((applied ?? string.Empty).Trim().Trim('"'), "1", StringComparison.Ordinal))
            {
                AppLog.Warn($"正文图片双击脚本未生效（返回 {applied}），图片无法双击放大");
            }
            else if (!_imageScriptReadyLogged)
            {
                // 每个进程记一次即可：用于确认「脚本禁用 + 宿主注入」这条路真的走通了
                _imageScriptReadyLogged = true;
                AppLog.Info("正文图片双击已启用（双击正文图片可放大查看）");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("正文图片双击脚本注入失败：" + ex.Message);
        }
    }

    /// <summary>正文脚本回传：用户双击了图片 → 取图片字节并打开放大查看窗口。</summary>
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        MailBodyImageMessage? message;
        try
        {
            message = MailBodyImageMessage.TryParse(e.WebMessageAsJson);
        }
        catch (Exception)
        {
            // 不是 JSON 的消息（理论上不会出现）：与正文无关，忽略
            return;
        }

        if (message is null) return;
        if (!Uri.TryCreate(message.Source, UriKind.Absolute, out var uri)) return;

        await OpenImageViewerAsync(uri, message.AltText);
    }

    /// <summary>
    /// 打开「查看大图」窗口：双击正文图片和右键「查看大图」共用这一条路径，
    /// 取字节（data: / 虚拟主机 / 网络）、解码、按宿主窗口居中弹出。
    /// </summary>
    private async Task OpenImageViewerAsync(Uri imageUri, string? altText)
    {
        var bytes = await TryReadUriAsync(imageUri);
        if (bytes is null || bytes.Length == 0)
        {
            AppLog.Warn($"正文图片读取失败，无法放大查看：{imageUri.Scheme}");
            return;
        }

        if (_browserReleased) return;

        var image = TryDecodeImage(bytes);
        if (image is null) return;

        var fileName = imageUri.Scheme == "data" ? "image" : Path.GetFileNameWithoutExtension(imageUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "image";

        var window = new MailImageViewerWindow(image, bytes, fileName, altText);
        if (Window.GetWindow(this) is { } owner && !ReferenceEquals(owner, window)) window.Owner = owner;
        window.Show();
    }

    /// <summary>把图片字节解码成可冻结的位图；不是图片或解码失败时返回 null。</summary>
    private static BitmapSource? TryDecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 正文右键菜单：默认菜单已关闭，这里只提供「复制 / 全选」。
    /// 页面脚本处于禁用状态，右键不会触发邮件自带行为。
    /// </summary>
    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        e.Handled = true;

        var target = e.ContextMenuTarget;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        // 和应用内其它右键菜单同一套外观（圆角卡片 + 主题配色），不再是系统灰底菜单
        if (FindStyle("MailContextMenu") is { } menuStyle) menu.Style = menuStyle;

        // 选中文本：复制
        if (target.HasSelection)
        {
            AddMenuItem(menu, "复制", "Ctrl+C", () => CopyToClipboard(target.SelectionText));
            AddSeparator(menu);
        }

        // 链接：新窗口打开 / 链接另存为 / 复制链接
        // （WebView2 的 ContextMenuTargetKind 只有 Page/Image/SelectedText 等，链接靠 HasLinkUri 判定）
        if (target.HasLinkUri
            && Uri.TryCreate(target.LinkUri, UriKind.Absolute, out var linkUri))
        {
            AddMenuItem(menu, "新窗口打开链接", null, () => OpenExternal(linkUri));
            AddMenuItem(menu, "链接另存为…", null, () => _ = SaveUriAsync(linkUri));
            AddMenuItem(menu, "复制链接", null, () => CopyToClipboard(target.LinkUri));
            AddSeparator(menu);
        }

        // 图片：查看大图（等同双击）/ 图片另存为 / 复制图片
        if (target.Kind == CoreWebView2ContextMenuTargetKind.Image
            && Uri.TryCreate(target.SourceUri, UriKind.Absolute, out var imageUri))
        {
            AddMenuItem(menu, "查看大图", "双击", () => _ = OpenImageViewerAsync(imageUri, null));
            AddMenuItem(menu, "图片另存为…", null, () => _ = SaveUriAsync(imageUri));
            AddMenuItem(menu, "复制图片", null, () => _ = CopyImageAsync(imageUri));
            AddSeparator(menu);
        }

        AddMenuItem(menu, "全选", "Ctrl+A", () =>
        {
            _ = SelectAllAsync();
        });

        menu.IsOpen = true;
    }

    private static void AddMenuItem(ContextMenu menu, string header, string? gesture, Action action)
    {
        var item = new MenuItem { Header = header };
        if (FindStyle("MailContextMenuItem") is { } itemStyle) item.Style = itemStyle;
        if (!string.IsNullOrEmpty(gesture)) item.InputGestureText = gesture;
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static void AddSeparator(ContextMenu menu)
        => menu.Items.Add(new Separator { Style = FindStyle("MailContextMenuSeparator") });

    /// <summary>取 App 级菜单样式；设计器里没有 Application 资源时返回 null，退回系统外观。</summary>
    private static Style? FindStyle(string key) => System.Windows.Application.Current?.TryFindResource(key) as Style;

    /// <summary>把链接 / 图片地址读成字节：虚拟主机映射到本地附件目录，data: 走 Base64，其余按网络下载。</summary>
    private async Task<byte[]?> TryReadUriAsync(Uri uri)
    {
        try
        {
            if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            {
                if (string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase))
                {
                    var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                    if (string.IsNullOrWhiteSpace(EmlDirectory)) return null;

                    var path = System.IO.Path.Combine(
                        EmlDirectory, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    return System.IO.File.Exists(path) ? await System.IO.File.ReadAllBytesAsync(path) : null;
                }

                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                return await http.GetByteArrayAsync(uri);
            }

            if (uri.Scheme == "data")
            {
                var comma = uri.OriginalString.IndexOf(',');
                if (comma < 0) return null;

                var meta = uri.OriginalString[..comma];
                var payload = uri.OriginalString[(comma + 1)..];
                return meta.Contains("base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            }
        }
        catch (Exception)
        {
            // 网络不可用 / 文件缺失 / 编码错误：由调用方提示
        }

        return null;
    }

    /// <summary>链接或图片另存为本地文件。</summary>
    private async Task SaveUriAsync(Uri uri)
    {
        var name = System.IO.Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name)) name = "download";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存为",
            FileName = name,
            Filter = "所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var bytes = await TryReadUriAsync(uri);
        if (bytes is null)
        {
            MessageDialog.Alert(
                "另存为",
                "无法获取该内容，可能是网络受限或文件已被清理。",
                owner: Window.GetWindow(this));
            return;
        }

        try
        {
            await System.IO.File.WriteAllBytesAsync(dialog.FileName, bytes);
        }
        catch (Exception ex)
        {
            MessageDialog.Alert("另存为", "保存失败：" + ex.Message, DialogIcon.Warning, owner: Window.GetWindow(this));
        }
    }

    /// <summary>把图片写入剪贴板（支持 PNG/JPEG/GIF/BMP 等位图格式）。</summary>
    private async Task CopyImageAsync(Uri uri)
    {
        var bytes = await TryReadUriAsync(uri);
        if (bytes is null)
        {
            MessageDialog.Alert(
                "复制图片",
                "无法读取该图片，可能是网络受限或文件已被清理。",
                owner: Window.GetWindow(this));
            return;
        }

        try
        {
            using var stream = new System.IO.MemoryStream(bytes);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Clipboard.SetImage(frame);
        }
        catch (Exception)
        {
            MessageDialog.Alert("复制图片", "该图片格式无法复制到剪贴板。", owner: Window.GetWindow(this));
        }
    }

    /// <summary>全选正文（宿主脚本不受页面脚本禁用限制）。</summary>
    private async Task SelectAllAsync()
    {
        if (_browserReleased || Browser.CoreWebView2 is not { } core) return;

        try
        {
            await core.ExecuteScriptAsync("document.execCommand('selectAll')");
            Browser.Focus();
        }
        catch (Exception)
        {
            // 内核未就绪或已崩溃：忽略
        }
    }

    /// <summary>
    /// 降级位面按纯文本复制：FlowDocument 内联图片时，WPF 默认的 XamlPackage
    /// 序列化会触发 FailFast（WpfPayload.AddImage），因此自行写入纯文本。
    /// </summary>
    private void OnPlainCopyExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        CopyToClipboard(PlainTextHost.Selection.Text);
        e.Handled = true;
    }

    /// <summary>把选中文本写入剪贴板；剪贴板被其它进程占用时静默跳过。</summary>
    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // 剪贴板被占用：不打断阅读
        }
    }

    /// <summary>全选正文：宿主注入的脚本不受页面脚本禁用限制。</summary>
    private async void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (_browserReleased || Browser.CoreWebView2 is not { } core) return;

        try
        {
            await core.ExecuteScriptAsync("document.execCommand('selectAll')");

            // 菜单关闭后键盘焦点会回到窗口；交还正文，用户可直接 Ctrl+C
            Browser.Focus();
        }
        catch (Exception)
        {
            // 内核未就绪或已崩溃：忽略
        }
    }

    private static void OpenExternal(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 系统无默认浏览器时静默失败，不影响阅读
        }
    }
}
