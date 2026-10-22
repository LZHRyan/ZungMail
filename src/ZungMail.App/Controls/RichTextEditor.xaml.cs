using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ZungMail.App.Services;
using ZungMail.Core.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace ZungMail.App.Controls;

/// <summary>
/// 富文本正文编辑器：WebView2 承载 <c>contenteditable</c> 页面，工具栏与查找替换在页面内实现。
/// 内容通过 <see cref="Html"/> 双向同步；WebView2 不可用时降级为纯文本输入，不阻断发送。
/// </summary>
public partial class RichTextEditor : UserControl
{
    private const string VirtualHost = "zungmail.editor";
    private const string EditorUrl = "https://zungmail.editor/editor.html";

    private CoreWebView2Environment? _environment;
    private bool _initialized;
    private bool _failed;

    /// <summary>WebView2 已释放（宿主窗口关闭后），禁止再初始化或访问内核。</summary>
    private bool _browserReleased;

    /// <summary>宿主窗口：关闭时释放 WebView2，否则写信窗口关一次就留一份渲染进程。</summary>
    private Window? _ownerWindow;

    /// <summary>正在把页面内容回写依赖属性，避免再推回页面造成回环。</summary>
    private bool _applyingEditorChange;

    /// <summary>降级位面同步标记，避免 TextChanged 与 Html 变更互相触发。</summary>
    private bool _syncingFallback;

    static RichTextEditor()
    {
        // 撰写正文字号随设置联动；页面内的编辑区字号由 setFontSize 推送
        FontSizeProperty.OverrideMetadata(
            typeof(RichTextEditor),
            new FrameworkPropertyMetadata(13.5, OnFontSizeChanged));
    }

    public RichTextEditor()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // ---------------- 事件 ----------------

    /// <summary>页面内 Ctrl+Enter 触发。</summary>
    public event EventHandler? SendRequested;

    /// <summary>页面内「附件标识 → 添加附件」触发，参数为选中的文件路径。</summary>
    public event EventHandler<IReadOnlyList<string>>? AttachmentPickRequested;

    // ---------------- 依赖属性 ----------------

    /// <summary>正文 HTML（与编辑区双向同步）。</summary>
    public static readonly DependencyProperty HtmlProperty = DependencyProperty.Register(
        nameof(Html), typeof(string), typeof(RichTextEditor),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnHtmlChanged));

    public string Html
    {
        get => (string)GetValue(HtmlProperty);
        set => SetValue(HtmlProperty, value);
    }

    /// <summary>当前主题，跟随主界面切换。</summary>
    public static readonly DependencyProperty IsDarkProperty = DependencyProperty.Register(
        nameof(IsDark), typeof(bool), typeof(RichTextEditor),
        new PropertyMetadata(false, OnIsDarkChanged));

    public bool IsDark
    {
        get => (bool)GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    /// <summary>附件文件名列表，用于页面内「插入附件标识」弹层。</summary>
    public static readonly DependencyProperty AttachmentNamesProperty = DependencyProperty.Register(
        nameof(AttachmentNames), typeof(IReadOnlyList<string>), typeof(RichTextEditor),
        new PropertyMetadata(null, OnAttachmentNamesChanged));

    public IReadOnlyList<string>? AttachmentNames
    {
        get => (IReadOnlyList<string>?)GetValue(AttachmentNamesProperty);
        set => SetValue(AttachmentNamesProperty, value);
    }

    /// <summary>富文本是否可用（WebView2 初始化成功）。</summary>
    public bool IsRichTextAvailable => _initialized && !_failed;

    /// <summary>空正文时编辑区内的占位提示文字。</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(RichTextEditor),
        new PropertyMetadata("在此输入邮件正文…", OnPlaceholderChanged));

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>
    /// 正文末尾的签名块（HTML）。为空表示不带签名；切换发件账户时整块替换。
    /// 由编辑器在 DOM 里独立维护，不写进 <see cref="Html"/>，因此不会打乱正文与撤销栈。
    /// </summary>
    public static readonly DependencyProperty SignatureProperty = DependencyProperty.Register(
        nameof(Signature), typeof(string), typeof(RichTextEditor),
        new PropertyMetadata(string.Empty, OnSignatureChanged));

    public string Signature
    {
        get => (string)GetValue(SignatureProperty);
        set => SetValue(SignatureProperty, value);
    }

    // ---------------- 生命周期 ----------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachOwnerWindow();

        if (_browserReleased) return;
        if (_initialized || _failed) return;

        // 设置页里「签名」「模板」整块是 Collapsed：等真正显示再建浏览器，
        // 否则每开一次设置都要多初始化一份渲染进程。
        if (!IsVisible)
        {
            IsVisibleChanged += OnVisibleChanged;
            return;
        }

        await EnsureCoreAsync();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_browserReleased) return;
        if (!IsVisible) return;

        IsVisibleChanged -= OnVisibleChanged;
        if (_initialized || _failed) return;
        _ = EnsureCoreAsync();
    }

    private void AttachOwnerWindow()
    {
        if (_ownerWindow is not null) return;

        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null) _ownerWindow.Closed += OnOwnerWindowClosed;
    }

    private void OnOwnerWindowClosed(object? sender, EventArgs e) => ReleaseBrowser();

    /// <summary>窗口关闭时释放 WebView2 与内核事件订阅，避免渲染进程滞留。</summary>
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
                core.ProcessFailed -= OnProcessFailed;
                core.WebMessageReceived -= OnWebMessageReceived;
            }
        }
        catch (Exception)
        {
            // 内核未初始化或已崩溃：没有可退订的订阅
        }

        try { Browser.Dispose(); }
        catch (Exception)
        {
            // 释放失败交给进程退出兜底
        }
    }

    private async Task EnsureCoreAsync()
    {
        try
        {
            var editorDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "editor");
            if (!WebViewRuntimeProbe.IsAvailable || !File.Exists(Path.Combine(editorDirectory, "editor.html")))
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

            // 撰写页需要脚本与消息桥；其余能力保持关闭，页面只加载本地资源
            var settings = core.Settings;
            settings.IsScriptEnabled = true;
            settings.IsWebMessageEnabled = true;
            settings.AreHostObjectsAllowed = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = true;

            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, editorDirectory, CoreWebView2HostResourceAccessKind.DenyCors);

            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.ProcessFailed += OnProcessFailed;
            core.WebMessageReceived += OnWebMessageReceived;

            _initialized = true;
            Browser.Visibility = Visibility.Visible;
            FallbackBox.Visibility = Visibility.Collapsed;

            ApplyBackgroundColor();
            core.Navigate(EditorUrl);
        }
        catch (Exception)
        {
            Fail();
        }
    }

    private void ApplyBackgroundColor()
        => Browser.DefaultBackgroundColor = IsDark
            ? System.Drawing.Color.FromArgb(0x16, 0x1B, 0x23)
            : System.Drawing.Color.White;

    /// <summary>降级为纯文本输入：保留已输入内容，发送链路继续可用。</summary>
    private void Fail()
    {
        if (_browserReleased) return;

        _failed = true;
        _initialized = false;
        Browser.Visibility = Visibility.Collapsed;
        FallbackBox.Visibility = Visibility.Visible;

        _syncingFallback = true;
        FallbackBox.Text = HtmlTextExtractor.ToPlainText(Html);
        _syncingFallback = false;
    }

    // ---------------- 消息桥 ----------------

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 页面统一 postMessage(对象)，此类消息只能通过 WebMessageAsJson 读取；
        // TryGetWebMessageAsString 仅适用于字符串消息，直接调用会抛异常。
        string? json;
        try
        {
            json = e.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            json = e.WebMessageAsJson;
        }

        if (string.IsNullOrEmpty(json)) return;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            switch (type)
            {
                case "ready":
                    await PushStateAsync();
                    break;

                case "change":
                    if (root.TryGetProperty("html", out var htmlElement) &&
                        htmlElement.ValueKind == JsonValueKind.String)
                    {
                        _applyingEditorChange = true;
                        Html = htmlElement.GetString() ?? string.Empty;
                        _applyingEditorChange = false;
                    }
                    break;

                case "send":
                    SendRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case "pickImage":
                    await PickImageAsync();
                    break;

                case "pickAttachment":
                    PickAttachments();
                    break;
            }
        }
        catch (Exception)
        {
            // 非法消息不打断编辑
        }
    }

    /// <summary>把宿主状态推入页面：主题、字号、占位提示、正文、签名、附件名。</summary>
    private async Task PushStateAsync()
    {
        await ExecuteAsync($"editorApi.setTheme({(IsDark ? "true" : "false")})");
        await ExecuteAsync($"editorApi.setFontSize({FontSize.ToString(CultureInvariant)})");
        await ExecuteAsync($"editorApi.setPlaceholder({JsonSerializer.Serialize(Placeholder ?? string.Empty)})");

        var html = Html;
        if (!string.IsNullOrEmpty(html))
        {
            await ExecuteAsync($"editorApi.setHtml({JsonSerializer.Serialize(html)})");
        }

        await PushSignatureAsync();
        await PushAttachmentNamesAsync();
    }

    /// <summary>把当前签名块推到页面：先清掉旧块再按最新账户内容重建。</summary>
    private Task PushSignatureAsync()
        => ExecuteAsync($"editorApi.setSignature({JsonSerializer.Serialize(Signature ?? string.Empty)})");

    private async Task PushAttachmentNamesAsync()
    {
        var names = (AttachmentNames ?? Array.Empty<string>()).ToArray();
        await ExecuteAsync($"editorApi.setAttachments({JsonSerializer.Serialize(names)})");
    }

    private async Task ExecuteAsync(string script)
    {
        if (_browserReleased || Browser.CoreWebView2 is not { } core) return;

        try
        {
            await core.ExecuteScriptAsync(script);
        }
        catch (Exception)
        {
            // 内核未就绪或已退出：下一次 ready 会重新推送
        }
    }

    // ---------------- 对外操作 ----------------

    /// <summary>读取编辑区最新 HTML；用于发送前冲刷防抖中的内容。</summary>
    public async Task<string?> GetHtmlAsync()
    {
        if (_browserReleased || Browser.CoreWebView2 is not { } core) return null;

        try
        {
            // JSON 编码后的字符串（含转义），直接作为 JS 值返回
            var result = await core.ExecuteScriptAsync("editorApi.getHtml()");
            return JsonSerializer.Deserialize<string>(result);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>插入图片（按 base64 data URI 进入正文，发送前再转 cid 内嵌资源）。</summary>
    public async Task InsertImageFileAsync(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > ComposeBodyBuilder.MaxInlineImageBytes) return;

            var bytes = await File.ReadAllBytesAsync(path);
            var dataUrl = $"data:{ResolveMediaType(info.Extension)};base64,{Convert.ToBase64String(bytes)}";
            await ExecuteAsync($"editorApi.insertImage({JsonSerializer.Serialize(dataUrl)})");
            await FocusEditorAsync();
        }
        catch (Exception)
        {
            // 文件读失败：忽略本次插入
        }
    }

    public Task InsertAttachmentTagAsync(string name)
        => ExecuteAsync($"editorApi.insertAttachment({JsonSerializer.Serialize(name)})");

    /// <summary>在光标处插入一段 HTML（模板内容块）。</summary>
    public Task InsertHtmlAsync(string html)
        => string.IsNullOrWhiteSpace(html)
            ? Task.CompletedTask
            : ExecuteAsync($"editorApi.insertHtml({JsonSerializer.Serialize(html)})");

    /// <summary>在光标处插入纯文本（模板占位符）。</summary>
    public Task InsertTextAsync(string text)
        => string.IsNullOrEmpty(text)
            ? Task.CompletedTask
            : ExecuteAsync($"editorApi.insertText({JsonSerializer.Serialize(text)})");

    /// <summary>
    /// 追加一段快速文本：插入到光标处并留出一个空行，随后焦点回到正文。
    /// WebView2 不可用时退化为纯文本框按光标位置插入，保证话术仍能落地。
    /// </summary>
    public async Task InsertQuickTextAsync(string text)
    {
        var value = (text ?? string.Empty).TrimEnd();
        if (value.Length == 0) return;

        if (IsRichTextAvailable)
        {
            await ExecuteAsync($"editorApi.insertQuickText({JsonSerializer.Serialize(value)})");
            await FocusEditorAsync();
            return;
        }

        var caret = FallbackBox.CaretIndex;
        var insertion = value + Environment.NewLine + Environment.NewLine;
        FallbackBox.Text = FallbackBox.Text.Insert(Math.Clamp(caret, 0, FallbackBox.Text.Length), insertion);
        FallbackBox.CaretIndex = caret + insertion.Length;
        FallbackBox.Focus();
    }

    public Task FocusEditorAsync() => ExecuteAsync("editorApi.focusEditor()");

    private async Task PickImageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "插入图片",
            Multiselect = false,
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*"
        };

        if (dialog.ShowDialog() != true) return;
        await InsertImageFileAsync(dialog.FileName);
    }

    /// <summary>页面内添加附件：交给窗口把文件加入附件条，再回推文件名列表。</summary>
    private void PickAttachments()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择附件",
            Multiselect = true,
            Filter = "所有文件|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        var files = dialog.FileNames.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
        if (files.Length == 0) return;

        AttachmentPickRequested?.Invoke(this, files);
    }

    private static string ResolveMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "image/png"
    };

    private static readonly System.Globalization.CultureInfo CultureInvariant = System.Globalization.CultureInfo.InvariantCulture;

    // ---------------- 属性变更 ----------------

    private static void OnHtmlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichTextEditor)d;
        if (editor._applyingEditorChange) return;

        if (editor.IsRichTextAvailable)
        {
            var html = e.NewValue as string ?? string.Empty;
            if (!string.IsNullOrEmpty(html))
            {
                _ = editor.SetHtmlAndKeepSignatureAsync(html);
            }
            return;
        }

        if (editor._failed)
        {
            editor._syncingFallback = true;
            editor.FallbackBox.Text = HtmlTextExtractor.ToPlainText(e.NewValue as string ?? string.Empty);
            editor._syncingFallback = false;
        }
    }

    /// <summary>整篇替换正文后重新贴上签名块（模板套用、冲刷回写都会走到这里）。</summary>
    private async Task SetHtmlAndKeepSignatureAsync(string html)
    {
        await ExecuteAsync($"editorApi.setHtml({JsonSerializer.Serialize(html)})");
        await PushSignatureAsync();
    }

    private static void OnSignatureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichTextEditor)d;
        if (editor.IsRichTextAvailable) _ = editor.PushSignatureAsync();
    }

    private static void OnFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichTextEditor)d;
        if (!editor.IsRichTextAvailable) return;

        _ = editor.ExecuteAsync(
            $"editorApi.setFontSize({((double)e.NewValue).ToString(CultureInvariant)})");
    }

    private static void OnIsDarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichTextEditor)d;
        if (editor._initialized) editor.ApplyBackgroundColor();
        if (editor.IsRichTextAvailable)
        {
            _ = editor.ExecuteAsync($"editorApi.setTheme({((bool)e.NewValue ? "true" : "false")})");
        }
    }

    private static void OnAttachmentNamesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichTextEditor)d;
        if (editor.IsRichTextAvailable) _ = editor.PushAttachmentNamesAsync();
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichTextEditor)d;
        if (!editor.IsRichTextAvailable) return;

        _ = editor.ExecuteAsync($"editorApi.setPlaceholder({JsonSerializer.Serialize(e.NewValue as string ?? string.Empty)})");
    }

    private void OnFallbackTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingFallback) return;

        _applyingEditorChange = true;
        Html = HtmlTextExtractor.ToHtml(FallbackBox.Text);
        _applyingEditorChange = false;
    }

    // ---------------- 导航守卫 ----------------

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Uri)) return;

        if (e.Uri.StartsWith(EditorUrl, StringComparison.OrdinalIgnoreCase)) return;
        if (e.Uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase)) return;

        // 编辑区内的链接不离开撰写页；外部地址交给系统浏览器
        e.Cancel = true;

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            OpenExternal(uri);
        }
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

    private static void OpenExternal(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 无默认浏览器时静默失败
        }
    }
}
