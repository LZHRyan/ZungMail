using System.Security.Cryptography;
using ZungMail.Update.Protocol;

namespace ZungMail.UpdateServer.Storage;

public sealed record PackageUpload(
    Stream Content,
    string FileName,
    long Length,
    string Version,
    string Channel,
    string Notes,
    bool Mandatory,
    string? MinVersion);

public sealed record PackageUploadResult(bool Success, int StatusCode, string? Error, ReleaseRecord? Record)
{
    public static PackageUploadResult Fail(int statusCode, string error) => new(false, statusCode, error, null);

    public static PackageUploadResult Ok(ReleaseRecord record) => new(true, 200, null, record);
}

/// <summary>
/// 安装包落盘：先流式写入临时文件并同时算 SHA256，全部校验通过后才移动到正式目录，
/// 任何失败都删掉半成品，避免管理页出现「有记录没文件」的脏数据。
/// </summary>
public sealed class PackageUploader
{
    private const int BufferSize = 1024 * 1024;
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private readonly UpdateServerOptions _options;
    private readonly ReleaseStore _store;
    private readonly ILogger<PackageUploader> _logger;

    public PackageUploader(UpdateServerOptions options, ReleaseStore store, ILogger<PackageUploader> logger)
    {
        _options = options;
        _store = store;
        _logger = logger;
    }

    public static bool TryNormalizeChannel(string? input, out string channel)
    {
        channel = (input ?? "").Trim().ToLowerInvariant();
        if (channel.Length == 0) channel = "stable";
        if (channel.Length > 16) return false;
        if (!char.IsAsciiLetterLower(channel[0])) return false;
        return channel.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-');
    }

    public async Task<PackageUploadResult> SaveAsync(PackageUpload upload, CancellationToken ct)
    {
        if (!UpdateVersion.TryParse(upload.Version, out var version))
        {
            return PackageUploadResult.Fail(400, "版本号格式不正确，请使用 1.2.3 或 1.2.3.4（可带 -beta.1 后缀）。");
        }

        if (!TryNormalizeChannel(upload.Channel, out var channel))
        {
            return PackageUploadResult.Fail(400, "渠道名只能包含小写字母、数字与连字符，且以字母开头。");
        }

        if (upload.Notes.Length > 20_000)
        {
            return PackageUploadResult.Fail(400, "更新说明过长（最多 20000 字）。");
        }

        var minVersion = "";
        if (!string.IsNullOrWhiteSpace(upload.MinVersion))
        {
            if (!UpdateVersion.TryParse(upload.MinVersion, out var min))
            {
                return PackageUploadResult.Fail(400, "最低可升级版本格式不正确。");
            }

            minVersion = min.ToString();
            if (min > version)
            {
                return PackageUploadResult.Fail(400, "最低可升级版本不能高于发布版本。");
            }
        }

        var extension = Path.GetExtension(upload.FileName).ToLowerInvariant();
        if (!_options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return PackageUploadResult.Fail(
                400,
                $"不支持的文件类型「{extension}」，请上传完整安装包（{string.Join(" / ", _options.AllowedExtensions)}）。");
        }

        if (upload.Length <= 0)
        {
            return PackageUploadResult.Fail(400, "安装包是空文件。");
        }

        if (upload.Length > _options.MaxUploadBytes)
        {
            return PackageUploadResult.Fail(
                413,
                $"安装包超过上限（{_options.MaxUploadBytes / 1024 / 1024} MB）。");
        }

        var normalizedVersion = version.ToString();
        if (_store.VersionExists(channel, normalizedVersion))
        {
            return PackageUploadResult.Fail(409, $"渠道 {channel} 下已存在版本 {normalizedVersion}。");
        }

        var releaseId = $"{channel}-{normalizedVersion}";
        var safeFileName = BuildSafeFileName(upload.FileName, extension);
        var tempPath = Path.Combine(_store.TempDirectory, Guid.NewGuid().ToString("N") + extension);

        long written = 0;
        string sha256;
        string? packageDirectory = null;
        try
        {
            await using (var output = new FileStream(
                             tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await upload.Content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (read <= 0) break;

                    written += read;
                    if (written > _options.MaxUploadBytes)
                    {
                        return PackageUploadResult.Fail(
                            413,
                            $"安装包超过上限（{_options.MaxUploadBytes / 1024 / 1024} MB）。");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                await output.FlushAsync(ct);
                sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (written == 0) return PackageUploadResult.Fail(400, "安装包是空文件。");

            packageDirectory = Path.Combine(_store.PackagesDirectory, releaseId);
            Directory.CreateDirectory(packageDirectory);
            var finalPath = Path.Combine(packageDirectory, safeFileName);
            File.Move(tempPath, finalPath, overwrite: true);

            var record = new ReleaseRecord
            {
                Id = releaseId,
                Version = normalizedVersion,
                Channel = channel,
                Notes = upload.Notes.Trim(),
                Mandatory = upload.Mandatory,
                MinVersion = string.IsNullOrEmpty(minVersion) ? null : minVersion,
                FileName = safeFileName,
                RelativePath = Path.Combine("packages", releaseId, safeFileName),
                Size = written,
                Sha256 = sha256,
                Published = false,
                CreatedAt = DateTimeOffset.UtcNow
            };

            var saved = await _store.AddAsync(record, ct);
            return PackageUploadResult.Ok(saved);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存安装包失败：{FileName}", upload.FileName);

            // 记录没落库时把可能已经移动过去的文件目录一并清掉，避免孤儿安装包
            if (packageDirectory is not null && _store.Find(releaseId) is null)
            {
                try { Directory.Delete(packageDirectory, recursive: true); }
                catch (Exception cleanupError) { _logger.LogWarning(cleanupError, "清理未完成的上传目录失败：{Path}", packageDirectory); }
            }

            return PackageUploadResult.Fail(500, "保存安装包失败：" + ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理临时上传文件失败：{Path}", tempPath);
            }
        }
    }

    private static string BuildSafeFileName(string original, string extension)
    {
        var name = Path.GetFileName(original);
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(InvalidFileNameChars.Contains(ch) ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim();
        if (sanitized.Length == 0) sanitized = "ZungMail-setup" + extension;
        if (sanitized.Length > 120) sanitized = sanitized[^120..];
        return sanitized;
    }
}
