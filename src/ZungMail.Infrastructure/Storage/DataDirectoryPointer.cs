using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZungMail.Infrastructure.Storage;

/// <summary>引导指针内容：当前生效的数据目录，以及待迁移的源目录。</summary>
public sealed class DataDirectoryState
{
    /// <summary>当前生效的数据目录（迁移完成后指向新目录）。</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>待迁移的源目录；为空表示没有待办迁移。</summary>
    public string? PendingSource { get; set; }
}

/// <summary>
/// 数据目录引导指针（<c>%APPDATA%\ZungMail\location.json</c>）。
/// 设置文件本身就在数据目录里，所以「数据目录在哪」必须存在这个固定位置，
/// 并且要在依赖注入、数据库连接打开之前读取。
/// <para>
/// 改名前（MailClient）用的是 <c>%APPDATA%\MailClient\location.json</c>：新指针不存在时会
/// 一次性接管那份旧数据（整目录改名，或把指针指过去），见 <see cref="Read"/>。
/// </para>
/// </summary>
public sealed class DataDirectoryPointer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string PointerFileName = "location.json";
    private const string CurrentDirectoryName = "ZungMail";
    private const string LegacyDirectoryName = "MailClient";

    /// <summary>累计上限：只是诊断用的几行说明，不需要无限增长。</summary>
    private const int MaxNotices = 8;

    private static readonly object NoticeLock = new();
    private static readonly List<string> Notices = new();

    /// <summary>
    /// 旧品牌数据目录被接管 / 搬迁时触发，参数是一行可直接落日志的说明（含新旧路径）。
    /// 用静态事件：<see cref="AppPaths"/> 在任何日志设施就绪之前就在构造函数里读指针，
    /// 只能全局订阅；订阅方抛异常不影响启动。
    /// </summary>
    public static event Action<string>? LegacyTakeover;

    /// <summary>
    /// 取出并清空累计的接管说明。启动早期还没有可用的 logger（读指针比建宿主更早），
    /// 所以先攒着，宿主起来后再落一行日志。
    /// </summary>
    public static IReadOnlyList<string> DrainNotices()
    {
        lock (NoticeLock)
        {
            var lines = Notices.ToArray();
            Notices.Clear();
            return lines;
        }
    }

    /// <summary>使用默认位置（<c>%APPDATA%\ZungMail</c>），并接管旧品牌的 <c>%APPDATA%\MailClient</c>。</summary>
    public DataDirectoryPointer()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), CurrentDirectoryName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyDirectoryName))
    {
    }

    /// <param name="defaultDirectory">默认数据目录，同时也是指针文件的存放目录。</param>
    /// <param name="legacyDefaultDirectory">
    /// 旧品牌版本的默认数据目录，用于一次性接管旧数据；为空表示不做旧目录接管（测试与自定义场景）。
    /// </param>
    public DataDirectoryPointer(string defaultDirectory, string? legacyDefaultDirectory = null)
    {
        DefaultDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(defaultDirectory));

        var legacy = Normalize(legacyDefaultDirectory);

        // 旧目录非法或与新目录重合时，没有可接管的东西
        LegacyDefaultDirectory = legacy is not null &&
                                 !string.Equals(legacy, DefaultDirectory, StringComparison.OrdinalIgnoreCase)
            ? legacy
            : null;
    }

    /// <summary>默认数据目录（同时也是指针文件所在目录）。</summary>
    public string DefaultDirectory { get; }

    /// <summary>旧品牌版本的默认数据目录；为空表示本实例不做旧目录接管。</summary>
    public string? LegacyDefaultDirectory { get; }

    /// <summary>指针文件路径。</summary>
    public string PointerPath => Path.Combine(DefaultDirectory, PointerFileName);

    /// <summary>旧版本的指针文件路径；没有旧目录时为 null。</summary>
    private string? LegacyPointerPath => LegacyDefaultDirectory is null
        ? null
        : Path.Combine(LegacyDefaultDirectory, PointerFileName);

    /// <summary>
    /// 读取指针；文件缺失、损坏或内容非法时回退到默认目录，绝不抛异常。
    /// <para>
    /// 纯读：**不搬目录、不写文件**。日志采集、启动环境快照与测试都会调用它，
    /// 读操作带副作用会让「跑一次测试就把用户数据搬走」这种事故变成可能。
    /// 旧品牌目录的接管由启动流程显式调用 <see cref="EnsureLegacyAdopted"/>。
    /// </para>
    /// </summary>
    public DataDirectoryState Read()
    {
        try
        {
            // 1) 新指针在：一切以它为准，绝不再看旧目录
            if (File.Exists(PointerPath))
            {
                return ReadState(PointerPath) ?? Fallback();
            }

            // 2) 新指针不在但旧指针在：按旧指针的结论返回（仍未接管，只是别丢数据）
            if (LegacyPointerPath is { } legacyPointer && File.Exists(legacyPointer))
            {
                return ReadState(legacyPointer) ?? Fallback();
            }

            return Fallback();
        }
        catch (Exception)
        {
            // 指针损坏不应导致启动失败：退回默认目录。
            return Fallback();
        }
    }

    /// <summary>
    /// 一次性接管旧品牌数据目录（写指针、必要时整目录改名）。
    /// <para>
    /// 只应由启动流程在「依赖注入与数据库连接之前」调用一次；新指针就位后重复调用无副作用。
    /// 旧数据就在旧默认目录里 → 整目录改名搬过来（同盘 rename，不复制文件）；
    /// 在自定义目录里 → 只把新指针指过去（原地接管）；搬不动 → 原地接管并记一行说明。
    /// </para>
    /// </summary>
    public DataDirectoryState EnsureLegacyAdopted()
    {
        try
        {
            // 已经接管过：以新指针为准，不再碰旧目录
            if (File.Exists(PointerPath))
            {
                return ReadState(PointerPath) ?? Fallback();
            }

            return AdoptLegacyDirectory() ?? Fallback();
        }
        catch (Exception)
        {
            return Fallback();
        }
    }

    private DataDirectoryState Fallback() => new() { DataDirectory = DefaultDirectory };

    /// <summary>
    /// 一次性接管旧品牌数据目录；返回 null 表示这台机器上没有旧数据，用新默认目录即可。
    /// </summary>
    private DataDirectoryState? AdoptLegacyDirectory()
    {
        var legacyDirectory = LegacyDefaultDirectory;
        var legacyPointer = LegacyPointerPath;
        if (legacyDirectory is null || legacyPointer is null) return null;

        DataDirectoryState? legacyState = null;
        if (File.Exists(legacyPointer))
        {
            legacyState = ReadState(legacyPointer);

            // 旧指针坏了：不猜它原本指向哪儿，直接用新默认目录，旧数据一个字节都不动
            if (legacyState is null) return Fallback();
        }
        else if (!Directory.Exists(legacyDirectory))
        {
            // 旧版本从没在这台机器上跑过
            return null;
        }

        // 旧指针缺失＝没写指针的老用户，数据就在旧默认目录里
        var source = legacyState?.DataDirectory ?? legacyDirectory;
        var usedOldDefault = string.Equals(source, legacyDirectory, StringComparison.OrdinalIgnoreCase);

        // 数据就在旧默认目录里：整目录搬到新默认目录（同盘 rename，700 MB 也是毫秒级）
        if (usedOldDefault && Directory.Exists(legacyDirectory))
        {
            if (TryMoveLegacyDefault(legacyDirectory, out var moveFailure))
            {
                return PersistAfterMove(legacyDirectory, legacyState?.PendingSource);
            }

            return Adopt(legacyDirectory, legacyState?.PendingSource, $"旧默认目录搬不动（{moveFailure}），原地接管");
        }

        // 用户当年把数据放到了自定义目录：不搬文件，只写新指针
        return Adopt(source, legacyState?.PendingSource, "接管旧版本的自定义数据目录（数据不搬动）");
    }

    /// <summary>搬迁成功后写新指针；写不进去也不能指回已经搬走的老路径。</summary>
    private DataDirectoryState PersistAfterMove(string legacyDirectory, string? legacyPending)
    {
        var state = new DataDirectoryState
        {
            DataDirectory = DefaultDirectory,
            PendingSource = FilterPending(legacyPending, DefaultDirectory)
        };

        // 搬过来的 location.json／.tmp 是旧指针，先清掉，免得被当成本次的新指针
        TryDelete(PointerPath);
        TryDelete(PointerPath + ".tmp");

        // rename 不复制文件，但库里的附件／eml／内嵌图片存的是绝对路径，必须跟着改到新根目录
        try
        {
            DataDirectoryService.RewriteRootPaths(legacyDirectory, DefaultDirectory);
        }
        catch (Exception ex)
        {
            Notify($"旧数据目录已搬到 {DefaultDirectory}，但库内绝对路径改写失败（{ex.GetType().Name}），附件可能显示为未下载。");
        }

        try
        {
            Write(state);
            Notify($"旧数据目录已迁移：{legacyDirectory} → {DefaultDirectory}");
        }
        catch (Exception ex)
        {
            // 只读盘等情况下写不进指针：数据已经在新目录里了，本次仍按新目录启动
            Notify($"旧数据目录已搬到 {DefaultDirectory}，但新指针写入失败（{ex.GetType().Name}）：{PointerPath}");
        }

        return state;
    }

    /// <summary>原地接管：只把新指针指向旧数据所在目录，文件一个都不动。</summary>
    private DataDirectoryState Adopt(string directory, string? legacyPending, string reason)
    {
        var state = new DataDirectoryState
        {
            DataDirectory = directory,
            PendingSource = FilterPending(legacyPending, directory)
        };

        try
        {
            Write(state);
            Notify($"{reason}：{directory}");
        }
        catch (Exception ex)
        {
            // 写不进新指针（只读盘、权限不足）：直接沿用旧状态，保证仍能启动、仍用旧数据
            Notify($"{reason}：{directory}，但新指针写入失败（{ex.GetType().Name}）：{PointerPath}");
        }

        return state;
    }

    /// <summary>
    /// 把旧默认目录整体改名成新默认目录（不复制数据）。失败时给出简短原因，调用方退回原地接管。
    /// </summary>
    private bool TryMoveLegacyDefault(string legacyDirectory, out string? reason)
    {
        reason = null;

        // 互相嵌套时 rename 会把数据搬进自己里面，直接放弃
        if (IsInside(DefaultDirectory, legacyDirectory) || IsInside(legacyDirectory, DefaultDirectory))
        {
            reason = "新旧目录互相嵌套";
            return false;
        }

        if (Directory.Exists(DefaultDirectory) && !TryDeleteEmptyDirectory(DefaultDirectory))
        {
            // 已经有一份新目录（比如改名后的版本已经跑过一次）：不删用户数据，退回原地接管
            reason = "新默认目录已存在且非空";
            return false;
        }

        try
        {
            var parent = Path.GetDirectoryName(DefaultDirectory);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            Directory.Move(legacyDirectory, DefaultDirectory);
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            return false;
        }

        // 搬走后若还残留空壳（例如只剩指针临时文件）就清掉；有用户文件一律保留
        TryRemoveEmptyShell(legacyDirectory);
        return true;
    }

    /// <summary>读一个指针文件并规范化；文件损坏或内容非法时返回 null（不抛异常）。</summary>
    private static DataDirectoryState? ReadState(string path)
    {
        try
        {
            var state = JsonSerializer.Deserialize<DataDirectoryState>(File.ReadAllText(path), Options);
            if (state is null) return null;

            var current = Normalize(state.DataDirectory);
            if (current is null) return null;

            return new DataDirectoryState
            {
                DataDirectory = current,
                PendingSource = FilterPending(state.PendingSource, current)
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>待迁移源归一化；与生效目录相同视为无效待办。</summary>
    private static string? FilterPending(string? pending, string current)
    {
        var normalized = Normalize(pending);
        return normalized is not null &&
               !string.Equals(normalized, current, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : null;
    }

    private static void Notify(string message)
    {
        lock (NoticeLock)
        {
            if (Notices.Count >= MaxNotices) Notices.RemoveAt(0);
            Notices.Add(message);
        }

        try
        {
            LegacyTakeover?.Invoke(message);
        }
        catch (Exception)
        {
            // 订阅方的问题不能影响启动
        }
    }

    private static bool IsInside(string child, string parent)
    {
        var prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>只删空目录（不递归）：有内容就返回 false，绝不碰用户文件。</summary>
    private static bool TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return true;
            if (Directory.EnumerateFileSystemEntries(path).Any()) return false;
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>清掉搬迁后可能残留的空壳目录（只由空文件／空子目录组成时）。</summary>
    private static void TryRemoveEmptyShell(string legacyDirectory)
    {
        try
        {
            if (!Directory.Exists(legacyDirectory)) return;
            if (Directory.EnumerateDirectories(legacyDirectory).Any()) return;

            foreach (var file in Directory.EnumerateFiles(legacyDirectory))
            {
                // 只清指针残留；有任何其它文件都说明不是空壳，整个目录保留
                var name = Path.GetFileName(file);
                if (!string.Equals(name, PointerFileName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, PointerFileName + ".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (new FileInfo(file).Length > 0) return;
            }

            foreach (var file in Directory.EnumerateFiles(legacyDirectory)) TryDelete(file);
            TryDeleteEmptyDirectory(legacyDirectory);
        }
        catch (Exception)
        {
            // 清理失败无所谓：留着空目录不影响任何逻辑
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 删不掉就留着，不影响启动
        }
    }

    /// <summary>写入指针（先写临时文件再替换，避免半截文件）。</summary>
    public void Write(DataDirectoryState state)
    {
        Directory.CreateDirectory(DefaultDirectory);
        var temp = PointerPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
        File.Move(temp, PointerPath, overwrite: true);
    }

    /// <summary>
    /// 当前实际应该用来读写文件的目录。
    /// <para>
    /// 有待迁移（<see cref="DataDirectoryState.PendingSource"/>）时仍用源目录：指针里的 DataDirectory
    /// 已经是目标目录，如果启动时直接按它建目录，日志/临时文件会先落进目标目录，
    /// 下一次预检就会因为「目标目录不是空的」把迁移挡在门外。
    /// 源目录不存在（被手工删掉）时才退回目标目录，迁移流程会把指针切成目标目录。
    /// </para>
    /// </summary>
    public static string ResolveEffective(DataDirectoryState state)
    {
        var pending = Normalize(state.PendingSource);
        if (pending is not null && Directory.Exists(pending)) return pending;

        return state.DataDirectory;
    }

    /// <summary>规范化并校验绝对路径；非法时返回 null。</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Path.IsPathFullyQualified(path)) return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
