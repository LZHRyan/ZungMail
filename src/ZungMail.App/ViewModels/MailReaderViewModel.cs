using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.Application.Services;
using ZungMail.App.Services;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Core.Services;
using ZungMail.Mail;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 独立阅读窗口（双击邮件列表弹出）的视图模型：始终只读一封邮件，
/// 复用主窗同一套两段式加载（邮件头先上屏、正文按需下载）与正文渲染参数。
/// 它不持有列表状态，所以窗口可以一直停留在一封邮件上，主窗口继续切换其它邮件互不影响。
/// </summary>
public sealed partial class MailReaderViewModel : ObservableObject
{
    private readonly MessageDetailLoader _detailLoader;
    private readonly MailTagService _tagService;
    private readonly AttachmentActionService _attachments;
    private readonly IAccountRepository _accounts;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settings;
    private readonly ThemeManager _theme;
    private readonly IAppPaths _paths;
    private readonly AvatarStore _avatars;
    private readonly IMailSourceWindowService _mailSourceWindows;

    /// <summary>「AI 总结」卡片状态（独立阅读窗口自己一份）。</summary>
    public MailSummaryViewModel Summary { get; }
    private readonly ILogger<MailReaderViewModel> _logger;

    /// <summary>本封邮件是否已放行远程图片（不持久化，切换邮件即复位）。</summary>
    private bool _loadRemoteImages;

    /// <summary>正文文档缓存：小正文就地组装，大正文（如 8MB 的水单邮件）在线程池组装。</summary>
    private readonly MailBodyDocumentCache _bodyDocument = new();

    /// <summary>窗口会话序号：窗口关闭时自增，用来丢弃关闭后才回来的加载结果。</summary>
    private int _session;

    /// <summary>本次打开的取消源：切换邮件或关窗时取消还没下完的正文，避免白占连接与流量。</summary>
    private CancellationTokenSource? _loadCts;

    public MailReaderViewModel(
        MessageDetailLoader detailLoader,
        MailTagService tagService,
        AttachmentActionService attachments,
        IAccountRepository accounts,
        IDialogService dialogs,
        ISettingsStore settings,
        ThemeManager theme,
        IAppPaths paths,
        AvatarStore avatars,
        IMailSourceWindowService mailSourceWindows,
        MailSummaryViewModel summary,
        ILogger<MailReaderViewModel> logger)
    {
        _detailLoader = detailLoader;
        _tagService = tagService;
        _attachments = attachments;
        _accounts = accounts;
        _dialogs = dialogs;
        _settings = settings;
        _theme = theme;
        _paths = paths;
        _avatars = avatars;
        _mailSourceWindows = mailSourceWindows;
        Summary = summary;
        _logger = logger;
        _avatars.Changed += OnAvatarsChanged;
    }

    // ---------------- 邮件头 ----------------

    [ObservableProperty]
    private EmailDetail? _detail;

    /// <summary>邮件头仍在读取（只覆盖本地查询那一瞬间）。</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>正文按需下载中：邮件头已经可读。</summary>
    [ObservableProperty]
    private bool _isBodyLoading;

    /// <summary>标题栏文案：主题 + 发件人，方便在任务栏 / Alt+Tab 里辨识。</summary>
    [ObservableProperty]
    private string _windowTitle = "邮件";

    /// <summary>底部状态行：正文下载进度、附件保存结果、读取失败原因。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>回复 / 回复全部 / 转发在独立阅读窗口里同样可用（草稿邮件除外）。</summary>
    public bool CanReplyOrForward => Detail is not null && MailActionGuards.CanReplyOrForward(Detail);

    [ObservableProperty]
    private IReadOnlyList<MailTag> _tags = Array.Empty<MailTag>();

    public bool HasDetail => Detail is not null;

    /// <summary>发件人头像（Gravatar 命中才有）；为空时界面继续用首字母色块。</summary>
    public ImageBrush? DetailAvatarBrush => _avatars.GetBrush(Detail?.FromEmail);

    public bool HasDetailAvatar => DetailAvatarBrush is not null;

    /// <summary>头像异步加载完成后刷新窗口里的发件人头像。</summary>
    private void OnAvatarsChanged(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(NotifyAvatarChanged);
            return;
        }

        NotifyAvatarChanged();
    }

    private void NotifyAvatarChanged()
    {
        OnPropertyChanged(nameof(DetailAvatarBrush));
        OnPropertyChanged(nameof(HasDetailAvatar));
    }

    public bool ShowLoading => Detail is null && IsLoading;

    public bool ShowPlaceholder => Detail is null && !IsLoading;

    public bool HasTags => Tags.Count > 0;

    /// <summary>附件区展示的普通附件；内嵌图片属于正文资源，不单独列出。</summary>
    public IReadOnlyList<AttachmentInfo> VisibleAttachments =>
        Detail?.Attachments.Where(a => !a.IsInline).ToList() ?? new List<AttachmentInfo>();

    public bool HasAttachments => VisibleAttachments.Count > 0;

    /// <summary>邮件头展示的原始大小；未知或零字节时显示占位而不是误导性的 0 B。</summary>
    public string SizeDisplayText => Detail is { Size: > 0 } detail
        ? DisplayFormat.FileSize(detail.Size)
        : "未知";

    // ---------------- 正文渲染 ----------------

    /// <summary>是否用 HTML 渲染正文（跟随设置页开关；关闭后回退纯文本）。</summary>
    public bool UseHtmlRendering => _settings.Current.HtmlRendering;

    public bool IsDark => _theme.IsDark;

    /// <summary>阅读窗格正文字号，跟随设置页的「紧凑 / 标准 / 较大」。</summary>
    public double BodyFontSize => _settings.Current.FontSize switch
    {
        "compact" => 12.5,
        "large" => 15.5,
        _ => 13.5
    };

    public string BodyDisplay => Detail is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(Detail.BodyText) ? "（此邮件暂无正文内容）" : Detail.BodyText;

    public string BodyHtmlSource => Detail?.BodyHtml ?? string.Empty;

    /// <summary>内联附件目录（正文控件映射为虚拟主机）。</summary>
    public string EmlDirectory => _paths.EmlDirectory;

    /// <summary>纯文本回退时可内联展示的图片路径（正文内嵌资源）。</summary>
    public IReadOnlyList<string> InlineImagePaths =>
        Detail?.Attachments
            .Where(a => a.IsInline && a.IsDownloaded && !string.IsNullOrEmpty(a.LocalPath))
            .Select(a => a.LocalPath!)
            .ToList() ?? new List<string>();

    /// <summary>正文 HTML 文档：主题、字号、远程图片开关与附件目录都参与组装（缓存 + 后台组装）。</summary>
    public string BodyHtmlDocument => _bodyDocument.Document;

    /// <summary>正文含远程图片且尚未放行时，显示「显示图片」提示条。</summary>
    public bool ShowRemoteImagesPrompt => UseHtmlRendering
        && Detail is not null
        && !_loadRemoteImages
        && _settings.Current.BlockRemoteImages
        && _bodyDocument.HasRemoteImages;

    /// <summary>
    /// 重算正文文档：小正文就地组装，大正文交给线程池，组装好了再通知界面。
    /// 以前这里是绑定 getter 里的同步组装，8MB 的邮件一开就占界面线程 200–300ms。
    /// </summary>
    private void RefreshBodyDocument()
    {
        if (!UseHtmlRendering || Detail is not { } detail)
        {
            _bodyDocument.Reset();
            OnPropertyChanged(nameof(BodyHtmlDocument));
            OnPropertyChanged(nameof(ShowRemoteImagesPrompt));
            return;
        }

        var options = CreateBodyDocumentOptions();
        var key = MailBodyDocumentFactory.BuildKey(detail, options);

        if (detail.BodyHtml is null || detail.BodyHtml.Length <= MailBodyDocumentCache.InlineBuildMaxChars)
        {
            if (_bodyDocument.TryBuildInline(key, () => MailBodyDocumentFactory.Build(detail, options)))
            {
                OnPropertyChanged(nameof(BodyHtmlDocument));
                OnPropertyChanged(nameof(ShowRemoteImagesPrompt));
            }

            return;
        }

        _ = BuildBodyDocumentAsync(detail, options, key);
    }

    private async Task BuildBodyDocumentAsync(EmailDetail detail, MailDocumentOptions options, string key)
    {
        // 结果回到界面线程后才赋值；被更新的请求取代时 BuildAsync 返回 false，不刷新界面
        if (!await _bodyDocument.BuildAsync(key, () => MailBodyDocumentFactory.Build(detail, options)).ConfigureAwait(true))
            return;

        OnPropertyChanged(nameof(BodyHtmlDocument));
        OnPropertyChanged(nameof(ShowRemoteImagesPrompt));
    }

    private MailDocumentOptions CreateBodyDocumentOptions() => MailBodyDocumentFactory.CreateOptions(
        IsDark,
        BodyFontSize,
        _loadRemoteImages || !_settings.Current.BlockRemoteImages,
        _settings.Current.FoldQuotedHistory,
        _settings.Current.FoldQuotedHistoryKeepLayers,
        EmlDirectory);

    /// <summary>正文区域是否显示正文：下载中先让位给加载占位，避免一大片空白。</summary>
    public bool ShowBodyView => !IsBodyLoading;

    /// <summary>
    /// 正文取不到时给一个「重新获取正文」入口：正文为空但状态已是「已下载」的邮件
    /// （上次解析失败 / 正文缓存被清 / 服务器返回空体）不会再自动重拉，只能由用户手动触发。
    /// 草稿不出现（正文是本地内容），规则见 <see cref="MailBodyRecovery"/>。
    /// </summary>
    public bool ShowReloadBodyButton => MailBodyRecovery.ShouldOfferReload(Detail, IsBodyLoading);

    /// <summary>
    /// 正文控件（含 WebView2）是否可见。
    /// WebView2 是独立 HWND，永远盖在同一层 Grid 里的 WPF 元素之上——只要它还在渲染，
    /// 叠在上面的「重新获取正文」卡片就看不见。所以需要卡片时把正文控件整块收起来。
    /// </summary>
    public bool ShowBodyContent => ShowBodyView && !ShowReloadBodyButton;

    /// <summary>对当前邮件生成 AI 摘要（本机小模型；不可用自动退回规则抽取）。</summary>
    [RelayCommand]
    private async Task SummarizeAsync() => await Summary.ShowAsync(Detail).ConfigureAwait(true);

    /// <summary>查看当前邮件源码：优先服务器原始 MIME，取不到回退本地重建；只读，不改已读状态。</summary>
    [RelayCommand]
    private void ViewMailSource()
    {
        if (Detail is not { } detail) return;
        _mailSourceWindows.Show(detail.Id, detail.SubjectDisplay);
    }

    /// <summary>重新拉取当前邮件的正文（忽略本地「已下载」标记），成功后就地刷新窗口内容。</summary>
    [RelayCommand]
    private async Task ReloadBodyAsync()
    {
        if (Detail is not { } header || IsBodyLoading) return;

        var version = _detailLoader.Begin();
        var session = _session;

        var previous = Interlocked.Exchange(ref _loadCts, null);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;

        IsBodyLoading = true;
        try
        {
            var refreshed = await _detailLoader.ReloadBodyAsync(header, version, text => StatusText = text, ct);
            if (refreshed is null || !_detailLoader.IsCurrent(version) || session != _session) return;

            if (!ReferenceEquals(refreshed, header)) Detail = refreshed;
            StatusText = "就绪";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 关窗或切换邮件：静默收尾
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "独立阅读窗口重新获取邮件 {Id} 正文失败", header.Id);
            StatusText = "重新获取正文失败：" + ex.Message;
        }
        finally
        {
            if (_detailLoader.IsCurrent(version)) IsBodyLoading = false;
        }
    }

    /// <summary>陌生发件人提示：设置开关打开（默认开）且这封确实是首次来信。</summary>
    public bool ShowDetailStranger => _settings.Current.MarkStrangerSenders && Detail is { IsStranger: true };

    /// <summary>退信提示：设置开关打开（默认开）且这封确实是投递失败退回的邮件。</summary>
    public bool ShowDetailBounce => _settings.Current.MarkBounceMails && Detail is { IsBounce: true };

    /// <summary>退信提示条第二行：失败收件人 / 状态码 / 诊断，没有细节时引导看正文。</summary>
    public string DetailBounceLine => Detail?.BounceDetailLine ?? "";

    // ---------------- 仿冒域名（钓鱼警示） ----------------

    /// <summary>自有企业域名（去掉公共邮箱域）；每次打开邮件时刷新一次。</summary>
    private IReadOnlySet<string> _ownDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>本封邮件的仿冒域名结论（呈现时算，不落库）。</summary>
    private PhishingDomainVerdict? _detailPhishing;

    /// <summary>独立阅读窗口是否显示红色钓鱼警示条。</summary>
    public bool ShowDetailPhishing => _detailPhishing is not null;

    /// <summary>钓鱼警示说明（与哪个自有域名相像、命中原因）。</summary>
    public string DetailPhishingHint => _detailPhishing?.Summary ?? "";

    private async Task LoadOwnDomainsAsync(CancellationToken ct)
    {
        try
        {
            var accounts = await _accounts.GetAllAsync(ct).ConfigureAwait(true);
            _ownDomains = PhishingDomainPolicy.OwnDomains(accounts.Select(account => account.Email));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 读账户失败不该影响阅读：退化成「没有自有域名」，只是不显示钓鱼提示
            _logger.LogDebug(ex, "读取账户域名失败（钓鱼警示）");
            _ownDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void RefreshDetailPhishing()
    {
        _detailPhishing = _settings.Current.PhishingDomainWarning && Detail is not null && _ownDomains.Count > 0
            ? PhishingDomainPolicy.Evaluate(Detail.FromEmail, _ownDomains, _settings.Current.PhishingDomainTolerance)
            : null;

        OnPropertyChanged(nameof(ShowDetailPhishing));
        OnPropertyChanged(nameof(DetailPhishingHint));
    }

    partial void OnDetailChanged(EmailDetail? value)
    {
        // 远程图片放行只对当前这封邮件生效
        _loadRemoteImages = false;

        // 换邮件就收起上一封的摘要
        Summary.Reset();

        RefreshDetailPhishing();
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailAvatarBrush));
        OnPropertyChanged(nameof(HasDetailAvatar));
        OnPropertyChanged(nameof(CanReplyOrForward));
        OnPropertyChanged(nameof(VisibleAttachments));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(InlineImagePaths));
        OnPropertyChanged(nameof(BodyDisplay));
        OnPropertyChanged(nameof(SizeDisplayText));
        OnPropertyChanged(nameof(BodyHtmlSource));
        RefreshBodyDocument();
        OnPropertyChanged(nameof(ShowDetailStranger));
        OnPropertyChanged(nameof(ShowDetailBounce));
        OnPropertyChanged(nameof(DetailBounceLine));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(UseHtmlRendering));
        OnPropertyChanged(nameof(IsDark));
        OnPropertyChanged(nameof(ShowReloadBodyButton));
        OnPropertyChanged(nameof(ShowBodyContent));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    partial void OnIsBodyLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBodyView));
        OnPropertyChanged(nameof(ShowReloadBodyButton));
        OnPropertyChanged(nameof(ShowBodyContent));
    }

    partial void OnTagsChanged(IReadOnlyList<MailTag> value) => OnPropertyChanged(nameof(HasTags));

    // ---------------- 加载 ----------------

    /// <summary>
    /// 打开（或切换）一封邮件：先本地邮件头上屏，正文未下载时再后台拉取。
    /// 窗口被复用时，后一次调用通过加载版本号丢弃过期结果。
    /// </summary>
    public async Task OpenAsync(long emailId)
    {
        var version = _detailLoader.Begin();
        var session = _session;

        // 切换邮件即取消上一封还没下完的正文
        var previous = Interlocked.Exchange(ref _loadCts, null);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var ct = cts.Token;

        Detail = null;
        Tags = Array.Empty<MailTag>();
        StatusText = "";
        WindowTitle = "邮件";
        IsLoading = true;
        IsBodyLoading = false;

        try
        {
            var header = await _detailLoader.LoadHeaderAsync(emailId, version, ct);
            if (!_detailLoader.IsCurrent(version) || session != _session) return;

            if (header is null)
            {
                StatusText = "邮件已不存在，可能已被删除或移动";
                return;
            }

            Detail = header;
            WindowTitle = header.SubjectDisplay;
            IsLoading = false;

            await LoadOwnDomainsAsync(ct);
            RefreshDetailPhishing();

            await LoadTagsAsync(header.Id);

            var needsBody = header.BodyState != BodyState.Downloaded;
            if (needsBody) IsBodyLoading = true;
            if (!needsBody) return;

            if (!_detailLoader.IsCurrent(version)) return;
            try
            {
                var full = await _detailLoader.LoadBodyAsync(header, version, text => StatusText = text, ct);
                if (full is null || !_detailLoader.IsCurrent(version) || session != _session) return;
                if (!ReferenceEquals(full, header)) Detail = full;
            }
            finally
            {
                if (_detailLoader.IsCurrent(version)) IsBodyLoading = false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 被下一次打开取代：静默收尾
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "独立阅读窗口读取邮件 {Id} 失败", emailId);
            StatusText = "读取邮件失败：" + ex.Message;
        }
        finally
        {
            if (_detailLoader.IsCurrent(version))
            {
                IsLoading = false;
                IsBodyLoading = false;
            }
        }
    }

    /// <summary>
    /// 窗口关闭时清空内容：本视图模型是 DI 单例，若不清空，
    /// 最后读过的那封邮件（正文 HTML + 附件列表）会一直驻留到进程退出。
    /// </summary>
    public void Reset()
    {
        // 自增会话号：关闭瞬间仍在进行的加载回来后会被丢弃，不会再把正文写回来
        _session++;

        // 关窗即清空摘要卡片并取消推理
        Summary.Reset();

        // 关窗即取消还没下完的正文，不必等下完
        var pending = Interlocked.Exchange(ref _loadCts, null);
        if (pending is not null)
        {
            pending.Cancel();
            pending.Dispose();
        }

        Detail = null;
        Tags = Array.Empty<MailTag>();
        StatusText = "";
        WindowTitle = "邮件";
        IsLoading = false;
        IsBodyLoading = false;
        _loadRemoteImages = false;
    }

    private async Task LoadTagsAsync(long emailId)
    {
        try
        {
            Tags = await _tagService.GetForMessageAsync(emailId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "独立阅读窗口读取标签失败");
            Tags = Array.Empty<MailTag>();
        }
    }

    /// <summary>本封邮件放行远程图片（仅本次阅读，不写入设置）。</summary>
    [RelayCommand]
    private void LoadRemoteImages()
    {
        if (_loadRemoteImages) return;

        _loadRemoteImages = true;
        RefreshBodyDocument();
        StatusText = "已加载远程图片";
    }

    // ---------------- 回复 / 回复全部 / 转发 ----------------

    [RelayCommand]
    private Task ReplyAsync() => ComposeAsync(ComposeMode.Reply);

    [RelayCommand]
    private Task ReplyAllAsync() => ComposeAsync(ComposeMode.ReplyAll);

    [RelayCommand]
    private Task ForwardAsync() => ComposeAsync(ComposeMode.Forward);

    /// <summary>作为附件转发：把原邮件重建成 .eml 挂到新邮件附件里。</summary>
    [RelayCommand]
    private async Task ForwardAsAttachmentAsync()
    {
        if (Detail is not { } detail)
        {
            StatusText = "请先选择一封邮件";
            return;
        }

        if (!detail.HasBody)
        {
            StatusText = "正文尚未下载：先打开这封邮件，再用「作为附件转发」";
            return;
        }

        try
        {
            var path = await Task.Run(() => MailEmlExporter.ExportAsync(detail, _paths.EmlDirectory));
            if (path is null)
            {
                StatusText = "正文尚未下载：先打开这封邮件，再用「作为附件转发」";
                return;
            }

            _dialogs.ShowCompose(new ComposeRequest
            {
                AccountId = detail.AccountId,
                Subject = ComposeRequestFactory.SubjectPrefix(detail.Subject, "Fwd:"),
                Body = string.Empty,
                Attachments = new[] { path }
            });

            StatusText = $"已把原邮件作为附件转发：{Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "作为附件转发失败：{EmailId}", detail.Id);
            StatusText = "作为附件转发失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 独立阅读窗口直接回复：撰写请求与主窗口共用 <see cref="ComposeRequestFactory"/>，
    /// 引用块、收件人与回复线索不会因为入口不同而变化。
    /// </summary>
    private async Task ComposeAsync(ComposeMode mode)
    {
        if (Detail is not { } detail)
        {
            StatusText = "请先选择一封邮件";
            return;
        }

        var self = await ResolveSelfAddressAsync(detail.AccountId);
        var request = ComposeRequestFactory.ForReply(
            detail,
            mode,
            self,
            path => ComposeRequestFactory.ReadLocalImage(path, _paths.EmlDirectory));

        if (request is null)
        {
            StatusText = "草稿邮件不能回复或转发，双击草稿可以继续编辑";
            return;
        }

        _dialogs.ShowCompose(request);
    }

    /// <summary>当前邮件所属账户的地址；取不到就返回空串（只影响「回复全部」是否排除自己）。</summary>
    private async Task<string> ResolveSelfAddressAsync(long accountId)
    {
        try
        {
            var account = await _accounts.GetAsync(accountId);
            return account?.Email ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "独立阅读窗口读取账户地址失败");
            return string.Empty;
        }
    }

    // ---------------- 附件（与主窗口共用 AttachmentActionService） ----------------

    [RelayCommand]
    private void PreviewAttachment(AttachmentInfo? attachment) => ApplyAttachmentStatus(_attachments.Preview(attachment));

    [RelayCommand]
    private void OpenAttachment(string? path) => ApplyAttachmentStatus(_attachments.Open(path));

    /// <summary>「仅收取正文」账户的附件按需下载：下载完成后重新载入当前邮件，附件即可打开。</summary>
    [RelayCommand]
    private async Task DownloadAttachmentAsync(AttachmentInfo? attachment)
    {
        StatusText = "正在下载附件…";
        ApplyAttachmentStatus(await _attachments.DownloadAsync(attachment));

        if (attachment is { EmailId: > 0 } file && Detail?.Id == file.EmailId)
        {
            await OpenAsync(file.EmailId);
        }
    }

    [RelayCommand]
    private void SaveAttachment(AttachmentInfo? attachment) => ApplyAttachmentStatus(_attachments.Save(attachment));

    [RelayCommand]
    private void RevealAttachment(AttachmentInfo? attachment) => ApplyAttachmentStatus(_attachments.Reveal(attachment));

    [RelayCommand]
    private void SaveAllAttachments() => ApplyAttachmentStatus(_attachments.SaveAll(VisibleAttachments));

    private void ApplyAttachmentStatus(string? message)
    {
        if (!string.IsNullOrEmpty(message)) StatusText = message;
    }
}

