using System.Text.Json;
using ZungMail.Update.Protocol;

namespace ZungMail.UpdateServer.Storage;

/// <summary>
/// 版本记录与安装包文件存储。
/// 没有数据库：一个 <c>releases.json</c> + <c>packages/{id}/</c> 目录，
/// 写入用「临时文件 + 覆盖移动」保证进程被杀也不会留下半个 JSON。
/// </summary>
public sealed class ReleaseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly UpdateServerOptions _options;
    private readonly ILogger<ReleaseStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _readLock = new();
    private List<ReleaseRecord> _releases = [];

    public ReleaseStore(UpdateServerOptions options, ILogger<ReleaseStore> logger)
    {
        _options = options;
        _logger = logger;
        DataDirectory = Path.GetFullPath(options.DataDirectory);
        CatalogPath = Path.Combine(DataDirectory, "releases.json");
        PackagesDirectory = Path.Combine(DataDirectory, "packages");
        TempDirectory = Path.Combine(DataDirectory, "tmp");
    }

    public string DataDirectory { get; }

    public string CatalogPath { get; }

    public string PackagesDirectory { get; }

    public string TempDirectory { get; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(PackagesDirectory);
        Directory.CreateDirectory(TempDirectory);

        if (!File.Exists(CatalogPath))
        {
            await PersistAsync([], ct);
            return;
        }

        try
        {
            await using var stream = File.OpenRead(CatalogPath);
            var file = await JsonSerializer.DeserializeAsync<ReleaseCatalogFile>(stream, JsonOptions, ct)
                       ?? new ReleaseCatalogFile();
            lock (_readLock)
            {
                _releases = file.Releases;
            }

            // 记录存在但文件丢了（手删 / 卷恢复不全）时标记出来，管理页会显示文件缺失
            foreach (var release in _releases)
            {
                if (!File.Exists(GetPackagePath(release)))
                {
                    _logger.LogWarning("版本 {Version} 的安装包文件缺失：{Path}", release.Version, GetPackagePath(release));
                }
            }
        }
        catch (Exception ex)
        {
            // 目录损坏不能让服务起不来：备份后以空目录继续，管理员可重新上传
            var backup = CatalogPath + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try { File.Move(CatalogPath, backup, overwrite: true); } catch { /* 备份失败不阻断 */ }
            _logger.LogError(ex, "releases.json 无法解析，已备份到 {Backup} 并以空目录启动", backup);
            lock (_readLock)
            {
                _releases = [];
            }
        }
    }

    public IReadOnlyList<ReleaseRecord> GetAll()
    {
        lock (_readLock)
        {
            return _releases.Select(r => r.Clone()).ToList();
        }
    }

    public ReleaseRecord? Find(string id)
    {
        lock (_readLock)
        {
            return _releases.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))?.Clone();
        }
    }

    public bool VersionExists(string channel, string version, string? exceptId = null)
    {
        lock (_readLock)
        {
            return _releases.Any(r =>
                string.Equals(r.Channel, channel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(r.Id, exceptId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>已发布版本里，渠道匹配且高于 <paramref name="current"/> 的最高版本。</summary>
    public ReleaseRecord? FindLatestPublished(UpdateVersion current, string channel)
    {
        lock (_readLock)
        {
            ReleaseRecord? best = null;
            UpdateVersion bestVersion = default;
            foreach (var release in _releases)
            {
                if (!release.Published) continue;
                if (!string.Equals(release.Channel, channel, StringComparison.OrdinalIgnoreCase)) continue;
                if (!UpdateVersion.TryParse(release.Version, out var version)) continue;
                if (version <= current) continue;
                // 文件被外部删除时不要把坏版本推给客户端，管理页会用「文件缺失」标出来
                if (!File.Exists(GetPackagePath(release))) continue;

                if (best is null || version > bestVersion)
                {
                    best = release;
                    bestVersion = version;
                }
            }

            return best?.Clone();
        }
    }

    public string GetPackagePath(ReleaseRecord release)
        => Path.GetFullPath(Path.Combine(DataDirectory, release.RelativePath));

    public async Task<ReleaseRecord> AddAsync(ReleaseRecord release, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            List<ReleaseRecord> next;
            lock (_readLock)
            {
                next = [.. _releases, release];
            }

            await PersistAsync(next, ct);
            lock (_readLock)
            {
                _releases = next;
            }

            return release.Clone();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>更新可编辑字段（版本、渠道、说明、强制、最低版本）。</summary>
    public async Task<ReleaseRecord?> UpdateAsync(
        string id,
        string version,
        string channel,
        string notes,
        bool mandatory,
        string? minVersion,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            List<ReleaseRecord> next;
            ReleaseRecord? updated = null;
            lock (_readLock)
            {
                next = _releases.Select(r => r.Clone()).ToList();
                var target = next.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (target is null) return null;

                target.Version = version;
                target.Channel = channel;
                target.Notes = notes;
                target.Mandatory = mandatory;
                target.MinVersion = minVersion;
                updated = target.Clone();
            }

            await PersistAsync(next, ct);
            lock (_readLock)
            {
                _releases = next;
            }

            return updated;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<ReleaseRecord?> SetPublishedAsync(string id, bool published, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            List<ReleaseRecord> next;
            ReleaseRecord? updated = null;
            lock (_readLock)
            {
                next = _releases.Select(r => r.Clone()).ToList();
                var target = next.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (target is null) return null;

                target.Published = published;
                target.PublishedAt = published ? (target.PublishedAt ?? DateTimeOffset.UtcNow) : null;
                updated = target.Clone();
            }

            await PersistAsync(next, ct);
            lock (_readLock)
            {
                _releases = next;
            }

            return updated;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>删除记录与安装包目录；返回 false 表示记录不存在。</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        ReleaseRecord? removed;
        await _writeLock.WaitAsync(ct);
        try
        {
            List<ReleaseRecord> next;
            lock (_readLock)
            {
                next = _releases.Where(r => !string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Clone())
                    .ToList();
                removed = _releases.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (removed is null) return false;
            }

            await PersistAsync(next, ct);
            lock (_readLock)
            {
                _releases = next;
            }
        }
        finally
        {
            _writeLock.Release();
        }

        try
        {
            var path = GetPackagePath(removed);
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除安装包文件失败：{Id}", id);
        }

        return true;
    }

    /// <summary>下载计数只在内存 + 延迟落盘，避免每次下载都等一次全量 JSON 写入。</summary>
    public async Task RecordDownloadAsync(string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            List<ReleaseRecord> next;
            lock (_readLock)
            {
                next = _releases.Select(r => r.Clone()).ToList();
                var target = next.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (target is null) return;
                target.DownloadCount++;
            }

            await PersistAsync(next, ct);
            lock (_readLock)
            {
                _releases = next;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记录下载次数失败：{Id}", id);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>删除上次异常退出留下的临时上传文件。</summary>
    public void CleanTempDirectory()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(TempDirectory))
            {
                try { File.Delete(file); } catch { /* 被占用则下次启动再清 */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理临时目录失败");
        }
    }

    private async Task PersistAsync(List<ReleaseRecord> releases, CancellationToken ct)
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = CatalogPath + ".tmp";
        var file = new ReleaseCatalogFile { Releases = releases };
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, file, JsonOptions, ct);
            await stream.FlushAsync(ct);
        }

        File.Move(temp, CatalogPath, overwrite: true);
    }
}
