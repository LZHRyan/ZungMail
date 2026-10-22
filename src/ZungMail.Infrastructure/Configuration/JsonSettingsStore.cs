using System.Text.Json;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZungMail.Infrastructure.Configuration;

/// <summary>应用设置持久化（JSON，原子写）。</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _logger;
    private AppSettings _current = new();

    public JsonSettingsStore(IAppPaths paths, ILogger<JsonSettingsStore>? logger = null)
    {
        _path = paths.SettingsPath;
        _logger = logger ?? NullLogger<JsonSettingsStore>.Instance;
    }

    public AppSettings Current => _current;

    public string? LoadWarning { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        LoadWarning = null;

        if (!File.Exists(_path))
        {
            _current = new AppSettings();
            return;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var loaded = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, ct).ConfigureAwait(false);
            _current = loaded ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            // 损坏文件改名保留（便于用户找回偏好），本次以默认值启动。
            var backup = BackupCorruptFile();
            _logger.LogWarning(ex, "设置文件损坏，已回退默认值；备份：{Backup}", backup ?? "（备份失败）");

            LoadWarning = backup is null
                ? "设置文件已损坏，本次使用默认设置；原文件未能备份。"
                : $"设置文件已损坏，已备份为 {Path.GetFileName(backup)}，本次使用默认设置。";
            _current = new AppSettings();
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, _current, Options, ct).ConfigureAwait(false);
        }
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>把损坏文件重命名为 settings.corrupt.{timestamp}.json；失败时返回 null。</summary>
    private string? BackupCorruptFile()
    {
        var backup = Path.Combine(
            Path.GetDirectoryName(_path)!, $"settings.corrupt.{DateTime.Now:yyyyMMdd-HHmmss}.json");

        try
        {
            File.Move(_path, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "备份损坏的设置文件失败");
            return null;
        }
    }
}
