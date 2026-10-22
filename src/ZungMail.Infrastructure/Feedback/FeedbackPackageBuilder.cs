using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ZungMail.Core.Abstractions;
using ZungMail.Update.Protocol;
using Microsoft.Extensions.Logging;

namespace ZungMail.Infrastructure.Feedback;

/// <summary>
/// 把用户在设置里填的内容组装成 <see cref="FeedbackRequest"/>：
/// 环境摘要只放版本 / 系统 / 规模统计，日志取最新文件末尾并做脱敏，
/// 总体积仍超过 <see cref="UpdateRoutes.MaxFeedbackBytes"/> 时继续截断日志而不是报错。
/// </summary>
public sealed class FeedbackPackageBuilder
{
    /// <summary>邮箱地址：日志里出现的一切邮箱都替换成占位符。</summary>
    private static readonly Regex EmailPattern = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Authorization 头：整行值都算凭据，一次清干净（日志前缀带时间戳，所以不锚定行首）。</summary>
    private static readonly Regex AuthorizationLinePattern = new(
        @"(?im)(\bauthorization\b\s*[:=]\s*).*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>password= / secret= / token= 一类的键值对，只保留键名。</summary>
    private static readonly Regex SecretValuePattern = new(
        @"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|client[_-]?secret)\b(\s*[=:]\s*)[^\s,;""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string RedactedMarker = "\n…（日志已截断，仅保留末尾片段）";

    private readonly ISettingsStore _settings;
    private readonly IApplicationVersion _version;
    private readonly IMessageRepository? _messages;
    private readonly IAccountRepository? _accounts;
    private readonly IFolderRepository? _folders;
    private readonly string _logDirectory;
    private readonly ILogger<FeedbackPackageBuilder>? _logger;

    public FeedbackPackageBuilder(
        ISettingsStore settings,
        IApplicationVersion version,
        IMessageRepository? messages = null,
        IAccountRepository? accounts = null,
        IFolderRepository? folders = null,
        string? logDirectory = null,
        ILogger<FeedbackPackageBuilder>? logger = null)
    {
        _settings = settings;
        _version = version;
        _messages = messages;
        _accounts = accounts;
        _folders = folders;
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZungMail", "logs");
        _logger = logger;
    }

    public string LogDirectory => _logDirectory;

    public FeedbackLogInfo InspectLog()
    {
        var file = FindLatestLog(out var error);
        if (file is null)
        {
            return FeedbackLogInfo.None(error ?? "日志目录里没有可附加的文件");
        }

        var size = Math.Min(file.Length, UpdateRoutes.MaxFeedbackLogBytes);
        return new FeedbackLogInfo(
            true,
            $"将附加 {file.Name} 的末尾 {FormatSize(size)}" + (file.Length > size ? "（文件更大，已截断）" : ""),
            size);
    }

    public async Task<FeedbackRequest> BuildRequestAsync(FeedbackDraft draft, CancellationToken ct = default)
    {
        var request = new FeedbackRequest
        {
            Category = FeedbackCategories.Normalize(draft.Category),
            Description = (draft.Description ?? "").Trim(),
            Contact = string.IsNullOrWhiteSpace(draft.Contact) ? null : draft.Contact!.Trim(),
            ClientVersion = _version.Version,
            Channel = string.IsNullOrWhiteSpace(_settings.Current.UpdateChannel)
                ? "stable"
                : _settings.Current.UpdateChannel.Trim(),
            SubmittedAt = DateTimeOffset.Now
        };

        if (draft.IncludeEnvironment)
        {
            request.Environment = await BuildEnvironmentAsync(ct).ConfigureAwait(false);
        }

        if (draft.IncludeLog)
        {
            var (log, truncated) = await ReadSanitizedLogAsync(ct).ConfigureAwait(false);
            request.Log = log;
            request.LogTruncated = truncated;
        }

        TrimToBudget(request);
        return request;
    }

    /// <summary>只保留版本、系统、规模与同步结论；不写邮箱、账户名、主题或正文。</summary>
    private async Task<string> BuildEnvironmentAsync(CancellationToken ct)
    {
        var settings = _settings.Current;
        var builder = new StringBuilder();
        builder.AppendLine("客户端版本：" + _version.Version);
        builder.AppendLine("升级渠道：" + (string.IsNullOrWhiteSpace(settings.UpdateChannel) ? "stable" : settings.UpdateChannel));
        builder.AppendLine("操作系统：" + RuntimeInformation.OSDescription.Trim());
        builder.AppendLine("系统架构：" + RuntimeInformation.OSArchitecture);
        builder.AppendLine("进程架构：" + RuntimeInformation.ProcessArchitecture);
        builder.AppendLine(".NET 运行时：" + Environment.Version);
        builder.AppendLine("处理器数：" + Environment.ProcessorCount);
        builder.AppendLine("主题：" + settings.Theme + " / 字号：" + settings.FontSize);
        builder.AppendLine("功能开关：货代 " + OnOff(settings.FreightFeaturesEnabled)
                                           + " / 会话 " + OnOff(settings.ConversationDefault));

        if (_accounts is not null)
        {
            try
            {
                builder.AppendLine("账户数：" + await _accounts.CountAsync(ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "反馈环境摘要：读取账户数失败");
            }
        }

        if (_messages is not null)
        {
            try
            {
                var total = await _messages.CountAsync(ct: ct).ConfigureAwait(false);
                var unread = await _messages.CountUnreadAsync(ct: ct).ConfigureAwait(false);
                builder.AppendLine($"本地邮件：{total} 封（未读 {unread}）");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "反馈环境摘要：读取邮件统计失败");
            }
        }

        if (_folders is not null)
        {
            try
            {
                var folders = await _folders.GetAllAsync(ct).ConfigureAwait(false);
                builder.AppendLine("文件夹数：" + folders.Count);
                var lastSync = folders
                    .Where(f => f.LastSyncUtc is not null)
                    .Select(f => f.LastSyncUtc!.Value)
                    .DefaultIfEmpty()
                    .Max();
                builder.AppendLine(lastSync == default
                    ? "上次同步：暂无记录"
                    : "上次同步：" + lastSync.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "反馈环境摘要：读取文件夹统计失败");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>取最新日志文件末尾 <see cref="UpdateRoutes.MaxFeedbackLogBytes"/> 字节并脱敏。</summary>
    private async Task<(string? Log, bool Truncated)> ReadSanitizedLogAsync(CancellationToken ct)
    {
        var file = FindLatestLog(out _);
        if (file is null) return (null, false);

        try
        {
            var limit = UpdateRoutes.MaxFeedbackLogBytes;
            var length = file.Length;
            var take = (int)Math.Min(length, limit);
            var buffer = new byte[take];

            await using var stream = new FileStream(
                file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                16 * 1024, useAsync: true);
            stream.Seek(length - take, SeekOrigin.Begin);

            var offset = 0;
            while (offset < take)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, take - offset), ct).ConfigureAwait(false);
                if (read <= 0) break;
                offset += read;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, offset);
            var truncated = length > take;

            // 从中间截断会切坏首行：丢掉第一段残缺行，避免日志开头出现乱码
            if (truncated)
            {
                var newline = text.IndexOf('\n');
                if (newline >= 0 && newline < 200) text = text[(newline + 1)..];
            }

            if (text.Length == 0) return (null, false);
            return (SanitizeLog(text), truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "读取日志文件失败：{Path}", file.FullName);
            return (null, false);
        }
    }

    /// <summary>邮箱 → &lt;email&gt;，口令 / 令牌 → &lt;redacted&gt;。</summary>
    public static string SanitizeLog(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sanitized = EmailPattern.Replace(text, "<email>");
        sanitized = AuthorizationLinePattern.Replace(sanitized, "$1<redacted>");
        sanitized = SecretValuePattern.Replace(sanitized, "$1$2<redacted>");
        return sanitized;
    }

    /// <summary>整体体积超过协议上限时继续砍日志：先减半，最后还是超就整段丢掉。</summary>
    private static void TrimToBudget(FeedbackRequest request)
    {
        var guard = 0;
        while (request.SerializedSize() > UpdateRoutes.MaxFeedbackBytes && !string.IsNullOrEmpty(request.Log))
        {
            request.LogTruncated = true;
            var log = request.Log!;

            if (guard++ >= 8 || log.Length < 512)
            {
                request.Log = null;
                break;
            }

            var keep = log.Length / 2;
            request.Log = log[..keep] + RedactedMarker;
        }
    }

    private FileInfo? FindLatestLog(out string? error)
    {
        error = null;
        try
        {
            if (!Directory.Exists(_logDirectory))
            {
                error = "日志目录不存在";
                return null;
            }

            var latest = new DirectoryInfo(_logDirectory)
                .EnumerateFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latest is null) error = "日志目录里没有可附加的文件";
            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "扫描日志目录失败：{Directory}", _logDirectory);
            error = "日志目录不可读";
            return null;
        }
    }

    private static string OnOff(bool value) => value ? "开" : "关";

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:0.#} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";
}
