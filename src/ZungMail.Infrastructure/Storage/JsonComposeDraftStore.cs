using System.Text.Json;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZungMail.Infrastructure.Storage;

/// <summary>
/// 写信草稿的本地 JSON 存储：一个草稿一个文件，放在数据目录的 <c>drafts/</c> 下。
/// 存储目录跟随数据目录，迁移目录时会一起复制。
/// </summary>
public sealed class JsonComposeDraftStore : IComposeDraftStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly ILogger<JsonComposeDraftStore> _logger;

    public JsonComposeDraftStore(IAppPaths paths, ILogger<JsonComposeDraftStore>? logger = null)
    {
        _directory = Path.Combine(paths.RootDirectory, "drafts");
        _logger = logger ?? NullLogger<JsonComposeDraftStore>.Instance;
    }

    public async Task<IReadOnlyList<ComposeDraft>> GetAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_directory)) return Array.Empty<ComposeDraft>();

        var drafts = new List<ComposeDraft>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                var draft = await JsonSerializer
                    .DeserializeAsync<ComposeDraft>(stream, Options, ct)
                    .ConfigureAwait(false);

                if (draft is null || string.IsNullOrWhiteSpace(draft.Id)) continue;
                drafts.Add(draft);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 单个草稿损坏不影响其它草稿，也不阻断写信
                _logger.LogWarning(ex, "读取草稿失败：{File}", file);
            }
        }

        return drafts
            .OrderByDescending(d => d.UpdatedUtc)
            .ToList();
    }

    public async Task SaveAsync(ComposeDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Id)) draft.Id = Guid.NewGuid().ToString("N");

        Directory.CreateDirectory(_directory);
        draft.UpdatedUtc = DateTimeOffset.UtcNow;

        var target = Path.Combine(_directory, draft.Id + ".json");
        // 唯一临时名 + 全程 ConfigureAwait(false)：写入不能被 UI 线程的 SynchronizationContext 卡住，
        // 否则关闭窗口时同步等待保存会死锁。
        var temp = Path.Combine(_directory, draft.Id + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            var stream = File.Create(temp);
            try
            {
                await JsonSerializer.SerializeAsync(stream, draft, Options, ct).ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
                // 清理失败不影响主流程
            }

            throw;
        }
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return Task.CompletedTask;

        var target = Path.Combine(_directory, id + ".json");
        try
        {
            if (File.Exists(target)) File.Delete(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "删除草稿失败：{File}", target);
        }

        return Task.CompletedTask;
    }
}
