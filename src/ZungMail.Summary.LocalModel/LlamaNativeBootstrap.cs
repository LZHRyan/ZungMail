using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace ZungMail.Summary.LocalModel;

/// <summary>
/// 原生库定位。LLamaSharp.Backend.Cpu 把 llama.dll 与依赖（ggml*.dll、mtmd.dll）按 CPU 指令集
/// 放在 <c>runtimes/win-x64/native/{avx512|avx2|avx|noavx}/</c> 下；LLamaSharp 只在
/// 「程序目录 + 系统目录」里按名字找 <c>llama</c>，而 Windows 解析依赖时也不会去 DLL 自己的目录，
/// 所以必须先让这一整套文件出现在程序目录（或显式设置的 DLL 搜索目录）里。
/// <para>
/// 打包时由 App 工程的 <c>FlattenLlamaNatives</c> 目标把选中变体复制到输出根目录；
/// 这里兜底开发期/漏打包的情况：程序目录缺少 llama.dll 时按 CPU 指令集从 runtimes 下复制一份，
/// 程序目录不可写（装在 Program Files）就落到 <c>%LOCALAPPDATA%\ZungMail\llama-native</c> 并设进搜索路径。
/// </para>
/// </summary>
internal static class LlamaNativeBootstrap
{
    private const string LlamaDll = "llama.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    private static int _configured;

    /// <summary>当前实际使用的原生库目录（诊断用）。</summary>
    public static string? NativeDirectory { get; private set; }

    /// <summary>必须在第一次创建 <c>ModelParams</c> 之前调用（首次调用后幂等）。</summary>
    public static void EnsureConfigured()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1) return;

        if (!OperatingSystem.IsWindows()) return;

        var appDirectory = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(appDirectory, LlamaDll)))
        {
            NativeDirectory = appDirectory;
            return;
        }

        var source = FindNativeSource();
        if (source is null) return;

        // 优先放程序目录：LLamaSharp 默认就在这里找，依赖也按同目录解析
        if (TryCopy(source, appDirectory))
        {
            NativeDirectory = appDirectory;
            return;
        }

        // 程序目录只读（Program Files 安装）：落到本机缓存并把目录加进 DLL 搜索路径
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZungMail", "llama-native", Path.GetFileName(source));

        if (TryCopy(source, fallback))
        {
            NativeDirectory = fallback;
            SetDllDirectory(fallback);
        }
    }

    /// <summary>按当前 CPU 挑指令集目录；找不到对应变体时退回 avx2 / 任意一套。</summary>
    private static string? FindNativeSource()
    {
        var root = AppContext.BaseDirectory;
        var variant = Avx512F.IsSupported ? "avx512" : Avx2.IsSupported ? "avx2" : Sse41.IsSupported ? "avx" : "noavx";

        var candidates = new[]
        {
            Path.Combine(root, "runtimes", "win-x64", "native", variant),
            Path.Combine(root, "runtimes", "win-x64", "native", "avx2"),
            Path.Combine(root, "runtimes", "win-x64", "native")
        };

        foreach (var directory in candidates)
        {
            if (File.Exists(Path.Combine(directory, LlamaDll))) return directory;
        }

        return null;
    }

    private static bool TryCopy(string source, string target)
    {
        try
        {
            Directory.CreateDirectory(target);

            foreach (var file in Directory.GetFiles(source, "*.dll"))
            {
                var destination = Path.Combine(target, Path.GetFileName(file));
                var sourceInfo = new FileInfo(file);

                if (File.Exists(destination) && new FileInfo(destination).Length == sourceInfo.Length) continue;

                File.Copy(file, destination, overwrite: true);
            }

            return File.Exists(Path.Combine(target, LlamaDll));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录只读 / 被占用：交给调用方决定下一处落点
            return false;
        }
    }
}
