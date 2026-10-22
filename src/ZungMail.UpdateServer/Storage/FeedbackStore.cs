using System.Text;
using System.Text.Json;
using ZungMail.Update.Protocol;

namespace ZungMail.UpdateServer.Storage;

/// <summary>
/// 用户反馈存储：一个 <c>feedback/</c> 目录，每条反馈一个 JSON + 可选同名 .log。
/// 没有数据库，写入用「临时文件 + 覆盖移动」；条数超过上限时按时间淘汰最旧的记录，
/// 避免客户端反复提交把数据卷占满。
/// </summary>
public sealed class FeedbackStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private const int DescriptionLimit = 4000;

    private readonly UpdateServerOptions _options;
    private readonly ILogger<FeedbackStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _readLock = new();
    private List<FeedbackRecord> _records = [];

    public FeedbackStore(UpdateServerOptions options, ILogger<FeedbackStore> logger)
    {
        _options = options;
        _logger = logger;
        DataDirectory = Path.GetFullPath(options.DataDirectory);
        FeedbackDirectory = Path.Combine(DataDirectory, "feedback");
    }

    public string DataDirectory { get; }

    public string FeedbackDirectory { get; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(FeedbackDirectory);

        var loaded = new List<FeedbackRecord>();
        foreach (var path in Directory.EnumerateFiles(FeedbackDirectory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var record = await JsonSerializer.DeserializeAsync<FeedbackRecord>(stream, JsonOptions, ct)
                    .ConfigureAwait(false);
                if (record is null || string.IsNullOrWhiteSpace(record.Id)) continue;

                // 文件名就是主键：手工改名或半截写入的残留直接忽略，避免列表里出现两条同 id
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), record.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                loaded.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "反馈文件无法解析，已跳过：{Path}", path);
            }
        }

        lock (_readLock)
        {
            _records = loaded.OrderByDescending(r => r.CreatedAt).ToList();
        }

        // 上次退出时数据卷超限（或手工塞了文件）：启动时先收敛到上限
        await TrimAsync(ct).ConfigureAwait(false);
    }

    public IReadOnlyList<FeedbackRecord> GetAll()
    {
        lock (_readLock)
        {
            return _records.Select(r => r.Clone()).ToList();
        }
    }

    public int Count
    {
        get { lock (_readLock) return _records.Count; }
    }

    public FeedbackRecord? Find(string id)
    {
        lock (_readLock)
        {
            return _records.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))?.Clone();
        }
    }

    /// <summary>读取单条日志正文；没有日志文件时返回 null。</summary>
    public async Task<string?> ReadLogAsync(string id, CancellationToken ct = default)
    {
        var path = LogPath(id);
        if (!File.Exists(path)) return null;

        try
        {
            return await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "读取反馈日志失败：{Id}", id);
            return null;
        }
    }

    public string LogPath(string id) => Path.Combine(FeedbackDirectory, SafeId(id) + ".log");

    public string MetaPath(string id) => Path.Combine(FeedbackDirectory, SafeId(id) + ".json");

    /// <summary>保存一条反馈；返回落盘后的记录（含生成的编号与时间）。</summary>
    public async Task<FeedbackRecord> AddAsync(
        FeedbackRequest request,
        string? log,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        var record = new FeedbackRecord
        {
            Id = NextId(now),
            Category = FeedbackCategories.Normalize(request.Category),
            Description = TrimTo(request.Description, DescriptionLimit),
            Contact = string.IsNullOrWhiteSpace(request.Contact) ? null : request.Contact!.Trim(),
            ClientVersion = TrimTo(request.ClientVersion, 40),
            Channel = string.IsNullOrWhiteSpace(request.Channel) ? null : request.Channel!.Trim(),
            Environment = string.IsNullOrWhiteSpace(request.Environment) ? null : request.Environment!.Trim(),
            LogTruncated = request.LogTruncated,
            CreatedAt = now
        };

        var payload = TruncateLog(log, _options.MaxFeedbackLogBytes);
        record.LogBytes = Encoding.UTF8.GetByteCount(payload ?? "");
        record.LogTruncated |= payload is not null && log is not null && payload.Length < log.Length;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(FeedbackDirectory);
            await WriteAtomicAsync(MetaPath(record.Id), record, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(payload))
            {
                var temp = LogPath(record.Id) + ".tmp";
                await File.WriteAllTextAsync(temp, payload, new UTF8Encoding(false), ct).ConfigureAwait(false);
                File.Move(temp, LogPath(record.Id), overwrite: true);
            }

            lock (_readLock)
            {
                _records = [record.Clone(), .. _records];
            }
        }
        finally
        {
            _writeLock.Release();
        }

        await TrimAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("收到用户反馈 {Id}（{Category}，日志 {Bytes} 字节）", record.Id, record.Category, record.LogBytes);
        return record.Clone();
    }

    /// <summary>删除记录与日志；返回 false 表示记录不存在。</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<FeedbackRecord> next;
            FeedbackRecord? removed;
            lock (_readLock)
            {
                removed = _records.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (removed is null) return false;

                next = _records
                    .Where(r => !string.Equals(r.Id, removed.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Clone())
                    .ToList();
            }

            TryDelete(MetaPath(removed.Id));
            TryDelete(LogPath(removed.Id));

            lock (_readLock)
            {
                _records = next;
            }

            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>反馈目录是否可写：运维用 <c>/healthz</c> 判断挂卷是否正常。</summary>
    public bool IsDirectoryWritable()
    {
        try
        {
            Directory.CreateDirectory(FeedbackDirectory);
            var probe = Path.Combine(FeedbackDirectory, ".health-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>条数超出上限时删掉最旧的记录（含日志）。</summary>
    private async Task TrimAsync(CancellationToken ct)
    {
        while (true)
        {
            FeedbackRecord? oldest;
            lock (_readLock)
            {
                if (_records.Count <= _options.MaxFeedbackRecords) return;
                oldest = _records[^1];
            }

            var removed = await DeleteAsync(oldest.Id, ct).ConfigureAwait(false);
            if (!removed) return;

            _logger.LogInformation("反馈条数超过上限 {Limit}，已淘汰最旧记录 {Id}", _options.MaxFeedbackRecords, oldest.Id);
        }
    }

    /// <summary>编号形如 <c>fb-20261019-001</c>：按天累计，文件被手工删除也不会重号。</summary>
    private string NextId(DateTimeOffset now)
    {
        var prefix = "fb-" + now.ToString("yyyyMMdd") + "-";
        var sequence = 1;
        lock (_readLock)
        {
            sequence = _records.Count(r => r.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) + 1;
        }

        while (true)
        {
            var candidate = prefix + sequence.ToString("000");
            if (!File.Exists(MetaPath(candidate))) return candidate;
            sequence++;
        }
    }

    private static async Task WriteAtomicAsync(string path, FeedbackRecord record, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 8 * 1024, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, record, JsonOptions, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>日志超过单条上限时只保留末尾片段（末尾才是崩溃现场）。</summary>
    private static string? TruncateLog(string? log, int limit)
    {
        if (string.IsNullOrEmpty(log)) return null;
        if (limit <= 0) limit = UpdateRoutes.MaxFeedbackLogBytes;
        if (Encoding.UTF8.GetByteCount(log) <= limit) return log;

        var bytes = Encoding.UTF8.GetBytes(log);
        var slice = new byte[limit];
        Array.Copy(bytes, bytes.Length - limit, slice, 0, limit);
        // 从中间切可能切坏多字节字符：丢掉首个不完整字符，避免落盘出现 U+FFFD
        var text = Encoding.UTF8.GetString(slice);
        var start = text.IndexOf('\n');
        if (start >= 0 && start < 200) text = text[(start + 1)..];
        return text;
    }

    private static string TrimTo(string? value, int limit)
    {
        var text = (value ?? "").Trim();
        return text.Length <= limit ? text : text[..limit];
    }

    private static string SafeId(string id)
    {
        var name = Path.GetFileName((id ?? "").Trim());
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) || ch == '.' ? '_' : ch);
        }

        var result = builder.ToString();
        return result.Length == 0 ? "unknown" : result;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除反馈文件失败：{Path}", path);
        }
    }
}
