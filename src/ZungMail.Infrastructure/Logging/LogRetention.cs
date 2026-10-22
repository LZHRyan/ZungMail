using System.Globalization;
using System.Security;
using ZungMail.Core.Domain;

namespace ZungMail.Infrastructure.Logging;

/// <summary>一次日志清理的结果：删掉几个、释放多少字节、几个删不掉。</summary>
public readonly record struct LogRetentionResult(int DeletedFiles, long DeletedBytes, int FailedFiles)
{
    public bool AnyDeleted => DeletedFiles > 0;
}

/// <summary>
/// 日志目录的保留期清理：只处理 <c>app-yyyyMMdd.log</c> 这种按天滚动的文件，
/// 早于保留期的直接删除；不认识的文件（用户自己放的、别的程序写的）一律不动。
/// <para>
/// 删除失败（被其它实例占用、权限不足、只读）只计数并回调告警，不抛异常——
/// 日志维护不能影响主流程，下一轮会重试。
/// </para>
/// </summary>
public static class LogRetention
{
    /// <summary>日志文件搜索模式（与 <see cref="FileLoggerProvider"/> 的命名保持一致）。</summary>
    public const string SearchPattern = "app-*.log";

    private const string Prefix = "app-";
    private const string Suffix = ".log";

    /// <summary>解析 <c>app-yyyyMMdd.log</c> 里的日期；命名不合规返回 false。</summary>
    public static bool TryParseDate(string? fileName, out DateOnly date)
    {
        date = default;
        var name = fileName ?? "";
        if (name.Length != Prefix.Length + 8 + Suffix.Length) return false;
        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return false;

        var digits = name.Substring(Prefix.Length, 8);
        foreach (var ch in digits)
        {
            if (!char.IsAsciiDigit(ch)) return false;
        }

        return DateOnly.TryParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);
    }

    /// <summary>
    /// 删除 <paramref name="directory"/> 下超过保留期的日志文件。
    /// </summary>
    /// <param name="directory">日志目录（不存在时直接返回零结果）。</param>
    /// <param name="keepDays">保留天数，会按 <see cref="LogRetentionPolicy"/> 夹到 1–7 天。</param>
    /// <param name="now">当前时间（测试可注入）；只用它的日期部分。</param>
    /// <param name="onWarning">单个文件删除失败/目录读取失败时的回调，参数是路径与异常。</param>
    public static LogRetentionResult Run(
        string directory,
        int keepDays,
        DateTime? now = null,
        Action<string, Exception>? onWarning = null)
    {
        var keep = LogRetentionPolicy.Normalize(keepDays);
        var today = DateOnly.FromDateTime(now ?? DateTime.Now);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new LogRetentionResult(0, 0, 0);
        }

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(directory).GetFiles(SearchPattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            onWarning?.Invoke(directory, ex);
            return new LogRetentionResult(0, 0, 0);
        }

        var deleted = 0;
        long deletedBytes = 0;
        var failed = 0;

        foreach (var file in files)
        {
            // 命名不合规的文件不是我们写的，绝不按猜测的日期删
            if (!TryParseDate(file.Name, out var fileDate)) continue;
            if (!LogRetentionPolicy.IsExpired(fileDate, today, keep)) continue;

            long length;
            try
            {
                length = file.Length;
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                failed++;
                onWarning?.Invoke(file.FullName, ex);
                continue;
            }

            deleted++;
            deletedBytes += length;
        }

        return new LogRetentionResult(deleted, deletedBytes, failed);
    }
}
