using System.Text.Json;
using ZungMail.Core.Abstractions;
using ZungMail.Core.Domain;
using ZungMail.Update.Protocol;

namespace ZungMail.Infrastructure.Update;

/// <summary>
/// 解析升级服务根地址：用户设置优先，其次安装目录的 <c>update.json</c>。
/// 只读文件、不缓存，运维替换 update.json 后下一次检测即生效。
/// </summary>
public sealed class UpdateAddressResolver
{
    public const string FileName = "update.json";

    private readonly string _installDirectory;

    public UpdateAddressResolver(string? installDirectory = null)
        => _installDirectory = installDirectory ?? AppContext.BaseDirectory;

    public string InstallFilePath => Path.Combine(_installDirectory, FileName);

    public UpdateAddressResolution Resolve(AppSettings settings)
    {
        var configured = (settings.UpdateServiceUrl ?? "").Trim();
        if (configured.Length > 0)
        {
            if (!UpdateAddress.TryNormalize(configured, out var address, out var error))
            {
                return UpdateAddressResolution.Invalid(UpdateAddressSource.UserSetting, "升级服务地址不合法：" + error);
            }

            return UpdateAddressResolution.Ok(UpdateAddressSource.UserSetting, address!);
        }

        if (!File.Exists(InstallFilePath))
        {
            return UpdateAddressResolution.Missing(
                "未配置升级服务地址。可在 设置 → 升级 中填写，或由管理员在安装目录放置 update.json。");
        }

        UpdateBootstrapConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<UpdateBootstrapConfig>(
                File.ReadAllText(InstallFilePath),
                UpdateJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return UpdateAddressResolution.Invalid(
                UpdateAddressSource.InstallFile,
                "安装目录的 update.json 无法读取或格式不正确，已按未配置处理。");
        }

        var url = (config?.UpdateServiceUrl ?? "").Trim();
        if (url.Length == 0)
        {
            return UpdateAddressResolution.Missing(
                "安装目录的 update.json 没有配置 updateServiceUrl，视为未配置升级服务。");
        }

        if (!UpdateAddress.TryNormalize(url, out var addressFromFile, out var fileError))
        {
            return UpdateAddressResolution.Invalid(
                UpdateAddressSource.InstallFile,
                "安装目录 update.json 里的地址不合法：" + fileError);
        }

        return UpdateAddressResolution.Ok(UpdateAddressSource.InstallFile, addressFromFile!);
    }

    /// <summary>渠道跟随地址来源：地址来自 update.json 时允许该文件同时指定渠道。</summary>
    public string ResolveChannel(AppSettings settings, UpdateAddressSource source)
    {
        var configured = (settings.UpdateChannel ?? "").Trim();
        if (source != UpdateAddressSource.InstallFile) return NormalizeChannel(configured);

        var fileChannel = ReadInstallFileChannel();
        return fileChannel is null ? NormalizeChannel(configured) : NormalizeChannel(fileChannel);
    }

    private string? ReadInstallFileChannel()
    {
        try
        {
            if (!File.Exists(InstallFilePath)) return null;
            var config = JsonSerializer.Deserialize<UpdateBootstrapConfig>(
                File.ReadAllText(InstallFilePath),
                UpdateJson.Options);
            var channel = (config?.Channel ?? "").Trim().ToLowerInvariant();
            return channel.Length == 0 ? null : channel;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>与升级服务一致：默认 stable，只接受小写字母、数字与连字符。</summary>
    public static string NormalizeChannel(string? channel)
        => string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim().ToLowerInvariant();
}
