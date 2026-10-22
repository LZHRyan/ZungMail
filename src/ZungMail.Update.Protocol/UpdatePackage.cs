namespace ZungMail.Update.Protocol;

/// <summary>升级包类型：决定客户端走「启动安装程序」还是「解压替换程序文件」。</summary>
public enum UpdatePackageKind
{
    /// <summary>扩展名不认识（服务端配置有误）。</summary>
    Unknown = 0,

    /// <summary>可执行的安装程序（.exe / .msi），装完可由安装器自动拉起客户端。</summary>
    Installer = 1,

    /// <summary>便携版压缩包（.zip），必须由用户手动解压覆盖，不能自动安装。</summary>
    Portable = 2
}

/// <summary>
/// 升级包扩展名的唯一判定处：升级服务白名单、客户端下载与界面文案都按这里的结果走，
/// 避免出现「能下载但不给装」这种前后不一致。
/// </summary>
public static class UpdatePackage
{
    /// <summary>安装程序扩展名（小写）。</summary>
    public static readonly string[] InstallerExtensions = [".exe", ".msi"];

    /// <summary>便携版扩展名（小写）。</summary>
    public static readonly string[] PortableExtensions = [".zip"];

    /// <summary>服务端与客户端共同接受的全部扩展名。</summary>
    public static readonly string[] AllowedExtensions = [".exe", ".msi", ".zip"];

    /// <summary>
    /// 取扩展名；输入可以是文件名、绝对路径或下载地址（会先去掉查询串与片段）。
    /// 认不出来时返回空字符串。
    /// </summary>
    public static string GetExtension(string? fileNameOrPath)
    {
        var text = (fileNameOrPath ?? "").Trim();
        if (text.Length == 0) return "";

        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0) text = text[..cut];

        var separator = text.LastIndexOfAny(['/', '\\']);
        if (separator >= 0) text = text[(separator + 1)..];
        if (text.Length == 0) return "";

        var dot = text.LastIndexOf('.');
        // 没有点，或点在首位（隐藏文件）都算没有扩展名
        if (dot <= 0 || dot == text.Length - 1) return "";

        return text[dot..].ToLowerInvariant();
    }

    /// <summary>按文件名 / 路径 / 下载地址判定包类型。</summary>
    public static UpdatePackageKind Classify(string? fileNameOrPath)
    {
        var extension = GetExtension(fileNameOrPath);
        if (InstallerExtensions.Contains(extension)) return UpdatePackageKind.Installer;
        if (PortableExtensions.Contains(extension)) return UpdatePackageKind.Portable;
        return UpdatePackageKind.Unknown;
    }

    /// <summary>是否为便携版压缩包。</summary>
    public static bool IsPortable(string? fileNameOrPath)
        => Classify(fileNameOrPath) == UpdatePackageKind.Portable;

    /// <summary>是否可以直接启动安装。</summary>
    public static bool IsInstaller(string? fileNameOrPath)
        => Classify(fileNameOrPath) == UpdatePackageKind.Installer;

    /// <summary>界面上展示的包类型名称。</summary>
    public static string Describe(string? fileNameOrPath) => Classify(fileNameOrPath) switch
    {
        UpdatePackageKind.Installer => "安装程序",
        UpdatePackageKind.Portable => "便携版 zip",
        _ => "未知类型"
    };
}
