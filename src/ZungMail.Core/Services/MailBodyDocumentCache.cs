using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ZungMail.Core.Domain;

namespace ZungMail.Core.Services;

/// <summary>组装结果：完整文档 + 是否含远程图片（提示条直接复用，不再重扫几 MB 的正文）。</summary>
public readonly record struct MailBodyDocument(string Html, bool HasRemoteImages);

/// <summary>
/// 正文文档缓存与组装调度。
///
/// 以前 <c>BodyHtmlDocument</c> 是绑定 getter 里的同步组装：8MB 的邮件一次 200–300ms，
/// 每次求值都落在界面线程上（日志里就是「UI 线程占用 215ms（阶段：正文组装）」）。
/// 现在按体积分流：小正文就地组装（几毫秒），大正文交给线程池，界面先上，好了再通知一次；
/// 同一个 key 只组装一次，主题/字号/远程图片等选项变了才重算。
/// </summary>
public sealed class MailBodyDocumentCache
{
    /// <summary>小于这个体积的正文就地组装：几毫秒的活开线程反而更慢，也避免正文闪空一下。</summary>
    public const int InlineBuildMaxChars = 96 * 1024;

    private string _document = string.Empty;
    private bool _hasRemoteImages;
    private string? _key;
    private int _version;

    /// <summary>当前可直接绑定的文档；尚未组装完时为空串。</summary>
    public string Document => _document;

    /// <summary>当前正文是否含远程图片（随文档一起算出）。</summary>
    public bool HasRemoteImages => _hasRemoteImages;

    /// <summary>清空缓存（换邮件、关掉 HTML 渲染、正文被重新下载）。</summary>
    public void Reset()
    {
        _key = null;
        _document = string.Empty;
        _hasRemoteImages = false;
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// 小正文就地组装；返回 false 表示命中缓存（同一个 key 已组装过），调用方不用再通知界面。
    /// </summary>
    public bool TryBuildInline(string key, Func<MailBodyDocument> factory)
    {
        if (IsCurrent(key)) return false;

        Interlocked.Increment(ref _version);
        Apply(key, factory());
        return true;
    }

    /// <summary>大正文后台组装；返回 false 表示结果已被更新的请求取代，调用方不要通知界面。</summary>
    public async Task<bool> BuildAsync(string key, Func<MailBodyDocument> factory)
    {
        if (IsCurrent(key)) return false;

        var version = Interlocked.Increment(ref _version);
        var built = await Task.Run(factory).ConfigureAwait(true);
        if (Volatile.Read(ref _version) != version) return false;

        Apply(key, built);
        return true;
    }

    private bool IsCurrent(string key) => _key is not null && string.Equals(_key, key, StringComparison.Ordinal);

    private void Apply(string key, MailBodyDocument document)
    {
        _key = key;
        _document = document.Html;
        _hasRemoteImages = document.HasRemoteImages;
    }
}

/// <summary>把邮件详情组装成渲染文档：选项、缓存键与组装本身集中在这里，两个 ViewModel 不再各写一份。</summary>
public static class MailBodyDocumentFactory
{
    /// <summary>空正文占位文档的源 HTML。</summary>
    private const string EmptyHtml = "<p class=\"mail-empty\">（此邮件暂无正文内容）</p>";

    /// <summary>组装选项：主题、字号、远程图片放行与引用折叠都影响输出，必须一起进缓存键。</summary>
    public static MailDocumentOptions CreateOptions(
        bool isDark,
        double fontSize,
        bool allowRemoteImages,
        bool foldQuotedHistory,
        int foldQuotedHistoryKeepLayers,
        string? emlDirectory)
        => new()
        {
            IsDark = isDark,
            FontSize = fontSize,
            AllowRemoteImages = allowRemoteImages,
            FoldQuotedHistory = foldQuotedHistory,
            FoldQuotedHistoryKeepLayers = foldQuotedHistoryKeepLayers,
            EmlDirectory = emlDirectory
        };

    /// <summary>
    /// 缓存键：同一封邮件的正文内容没变就不重复组装。
    /// 正文重新下载后长度会变（或者更新了正文状态），键随之改变。
    /// </summary>
    public static string BuildKey(EmailDetail? detail, MailDocumentOptions options)
    {
        if (detail is null) return string.Empty;

        return string.Join('|',
            detail.Id,
            (int)detail.BodyState,
            detail.BodyHtml?.Length ?? -1,
            detail.BodyText?.Length ?? -1,
            options.IsDark ? 1 : 0,
            options.FontSize.ToString("0.#", CultureInfo.InvariantCulture),
            options.AllowRemoteImages ? 1 : 0,
            options.FoldQuotedHistory ? 1 : 0,
            options.FoldQuotedHistoryKeepLayers,
            options.EmlDirectory ?? string.Empty);
    }

    /// <summary>组装文档（含「是否含远程图片」），纯计算，可在线程池执行。</summary>
    public static MailBodyDocument Build(EmailDetail? detail, MailDocumentOptions options)
    {
        var html = detail?.BodyHtml;
        var empty = string.IsNullOrWhiteSpace(html) && string.IsNullOrWhiteSpace(detail?.BodyText);

        var document = empty
            ? HtmlMailDocumentBuilder.Build(EmptyHtml, options)
            : HtmlMailDocumentBuilder.Build(detail!, options);

        return new MailBodyDocument(document, HtmlMailDocumentBuilder.ContainsRemoteImages(html));
    }
}
