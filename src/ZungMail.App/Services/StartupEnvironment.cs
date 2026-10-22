using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Logging;
using ZungMail.Infrastructure.Storage;

namespace ZungMail.App.Services;

/// <summary>
/// 启动环境快照：操作系统版本、CPU / 内存、显卡与 DPI、运行时、数据目录所在磁盘。
/// <para>
/// 「这台机器慢」光看分段计时只能定位到哪一段，还要知道这台机器是什么配置：
/// 同样的 2 秒 ACL 传播，SSD 上是 2 秒，网络盘上就是几分钟；
/// 渲染 Tier 0 说明没有硬件加速，WPF 首次绘制会成倍变慢。
/// 全部是只读探测（注册表 / 一个内核调用 / 文件大小），实测总耗时约 5 ms，不写任何数据。
/// </para>
/// </summary>
public static class StartupEnvironment
{
    private const string Prefix = "启动环境：";

    /// <summary>采集环境信息（每行一条，已带「启动环境：」前缀），失败项降级为占位文本。</summary>
    public static IReadOnlyList<string> Describe()
    {
        var lines = new List<string>(DescribeCore());
        lines.AddRange(DescribeGraphicsLines());
        return lines;
    }

    /// <summary>
    /// 同步写「与首屏无关」的几行：系统 / CPU 与内存 / 进程 / 数据目录。
    /// 只碰注册表、一个内核调用和文件大小，实测几毫秒，放在窗口出现之前也不会拖慢启动。
    /// </summary>
    public static void LogCore()
    {
        foreach (var line in DescribeCore())
        {
            Write(line);
        }
    }

    /// <summary>
    /// 首帧之后再写图形 / WebView2：这两项要拉起 WinForms、WPF 媒体与 WebView2 探测，
    /// 合计上百毫秒，没必要挤在「窗口迟迟不出现」的那条路径上。
    /// </summary>
    public static void LogGraphics()
    {
        foreach (var line in DescribeGraphicsLines())
        {
            Write(line);
        }
    }

    private static IReadOnlyList<string> DescribeCore()
        => new[]
        {
            DescribeOs(),
            DescribeCpuAndMemory(),
            DescribeProcess(),
            DescribeStorage()
        };

    private static IReadOnlyList<string> DescribeGraphicsLines()
        => new[] { DescribeGraphics(), DescribeWebView() };

    /// <summary>写日志（AppLog 不依赖 DI，宿主没起来也能记）。</summary>
    private static void Write(string line)
    {
        try
        {
            AppLog.Info(line);
        }
        catch (Exception)
        {
            // 埋点失败不影响启动
        }
    }

    /// <summary>与启动耗时强相关的设置项，单独一行（设置读出来之后再记）。</summary>
    public static string DescribeSettings(AppSettings settings)
    {
        var sb = new StringBuilder(Prefix).Append("设置=");
        sb.Append("启动即同步 ").Append(settings.SyncOnLaunch ? "是" : "否");
        sb.Append(" · 同步间隔 ").Append(AppSettings.EffectiveSyncIntervalMinutes(settings.SyncIntervalMinutes)).Append(" 分钟");
        sb.Append(" · 列表每页 ").Append(Math.Clamp(settings.ListPageSize, 10, 500)).Append(" 封");
        sb.Append(" · 会话模式 ").Append(settings.ConversationDefault ? "开" : "关");
        sb.Append(" · 自动清理 ").Append(settings.AutoCleanupEnabled ? "开" : "关");
        sb.Append(" · 退出清正文缓存 ").Append(settings.ClearBodyCacheOnExit ? "是" : "否");
        return sb.ToString();
    }

    public static void LogSettings(AppSettings settings)
    {
        try
        {
            AppLog.Info(DescribeSettings(settings));
        }
        catch (Exception)
        {
            // 同上
        }
    }

    // ---------------- 系统 ----------------

    private static string DescribeOs()
    {
        var sb = new StringBuilder(Prefix).Append("系统=");

        try
        {
            sb.Append(RuntimeInformation.OSDescription);

            var (product, display, build, ubr) = ReadWindowsVersion();
            if (product.Length > 0)
            {
                sb.Append(" / ").Append(product);
                if (display.Length > 0) sb.Append(' ').Append(display);
            }

            if (build.Length > 0)
            {
                sb.Append(" / Build ").Append(build);
                if (ubr > 0) sb.Append('.').Append(ubr.ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(" / ").Append(Environment.Is64BitOperatingSystem ? "64 位" : "32 位");
            sb.Append(" / 会话=").Append(IsRemoteSession() ? "远程桌面(RDP，WPF 走软件渲染)" : "本地控制台");
            sb.Append(" / 已开机 ").Append(FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)));
        }
        catch (Exception ex)
        {
            sb.Append("读取失败(").Append(ex.GetType().Name).Append(')');
        }

        return sb.ToString();
    }

    private static (string Product, string Display, string Build, int Ubr) ReadWindowsVersion()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return ("", "", "", 0);

            return (
                key.GetValue("ProductName") as string ?? "",
                key.GetValue("DisplayVersion") as string ?? "",
                key.GetValue("CurrentBuildNumber") as string ?? "",
                key.GetValue("UBR") is int ubr ? ubr : 0);
        }
        catch (Exception)
        {
            return ("", "", "", 0);
        }
    }

    private static bool IsRemoteSession()
    {
        try
        {
            return GetSystemMetrics(SmRemoteSession) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------- CPU / 内存 ----------------

    private static string DescribeCpuAndMemory()
    {
        var sb = new StringBuilder(Prefix).Append("CPU=");

        try
        {
            sb.Append(ReadProcessorName());
            sb.Append(" / 逻辑核 ").Append(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            sb.Append("读取失败(").Append(ex.GetType().Name).Append(')');
        }

        try
        {
            if (TryGetMemory(out var totalBytes, out var availableBytes, out var loadPercent))
            {
                sb.Append(" / 内存 ").Append(ToGb(totalBytes)).Append("GB（可用 ")
                  .Append(ToGb(availableBytes)).Append("GB，已用 ")
                  .Append(loadPercent.ToString(CultureInfo.InvariantCulture)).Append("%）");
            }
        }
        catch (Exception)
        {
            // 内存信息只是参考，读不到就不写
        }

        return sb.ToString();
    }

    private static string ReadProcessorName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            return string.IsNullOrWhiteSpace(name) ? "未知" : name!.Trim();
        }
        catch (Exception)
        {
            return "未知";
        }
    }

    private static bool TryGetMemory(out ulong totalBytes, out ulong availableBytes, out uint loadPercent)
    {
        totalBytes = 0;
        availableBytes = 0;
        loadPercent = 0;

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return false;

        totalBytes = status.TotalPhys;
        availableBytes = status.AvailablePhys;
        loadPercent = status.MemoryLoad;
        return true;
    }

    // ---------------- 图形 ----------------

    private static string DescribeGraphics()
    {
        var sb = new StringBuilder(Prefix).Append("图形=");

        try
        {
            var tier = System.Windows.Media.RenderCapability.Tier >> 16;
            sb.Append("WPF 渲染 Tier ").Append(tier.ToString(CultureInfo.InvariantCulture))
              .Append('（').Append(tier switch
              {
                  0 => "无硬件加速，纯软件渲染",
                  1 => "部分硬件加速",
                  2 => "完全硬件加速",
                  _ => "未知"
              }).Append('）');
        }
        catch (Exception)
        {
            sb.Append("渲染层级未知");
        }

        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen is not null)
            {
                var pixelWidth = screen.Bounds.Width;
                var dipWidth = System.Windows.SystemParameters.PrimaryScreenWidth;
                var scale = dipWidth > 0 ? pixelWidth / dipWidth : 1.0;

                sb.Append(" / 主屏 ").Append(pixelWidth.ToString(CultureInfo.InvariantCulture))
                  .Append('x').Append(screen.Bounds.Height.ToString(CultureInfo.InvariantCulture))
                  .Append(" / 缩放 ").Append(scale.ToString("P0", CultureInfo.InvariantCulture))
                  .Append(" / 显示器 ").Append(System.Windows.Forms.Screen.AllScreens.Length)
                  .Append(" 台");
            }
        }
        catch (Exception)
        {
            // 屏幕信息读不到不影响启动
        }

        return sb.ToString();
    }

    // ---------------- 进程 / 运行时 ----------------

    private static string DescribeProcess()
    {
        var sb = new StringBuilder(Prefix).Append("进程=");

        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(StartupEnvironment).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString()
                          ?? "未知";

            sb.Append("ZungMail ").Append(version.Split('+')[0]);
            sb.Append(" / ").Append(RuntimeInformation.FrameworkDescription);
            sb.Append(" / ").Append(RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
            sb.Append(" / ").Append(IsSelfContained() ? "自包含" : "框架依赖");

            var workingSetMb = Environment.WorkingSet / (1024 * 1024);
            sb.Append(" / 工作集 ").Append(workingSetMb.ToString(CultureInfo.InvariantCulture)).Append("MB");
            sb.Append(" / 目录 ").Append(AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            sb.Append("读取失败(").Append(ex.GetType().Name).Append(')');
        }

        return sb.ToString();
    }

    private static bool IsSelfContained()
    {
        try
        {
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------- 数据目录 / 磁盘 ----------------

    private static string DescribeStorage()
    {
        var sb = new StringBuilder(Prefix).Append("数据目录=");

        try
        {
            var paths = new AppPaths();
            var root = paths.RootDirectory;

            sb.Append(root);
            sb.Append(" / 磁盘=").Append(DescribeDrive(root));

            var database = new FileInfo(paths.DatabasePath);
            if (database.Exists)
            {
                sb.Append(" / mail.db ").Append((database.Length / (1024.0 * 1024)).ToString("F0", CultureInfo.InvariantCulture)).Append("MB");
            }
            else
            {
                sb.Append(" / mail.db 尚未创建");
            }

            // 这两类位置会让 SQLite（尤其 WAL）性能下降一个数量级，值得单独提示
            if (IsUnderSyncFolder(root)) sb.Append(" / 注意：位于 OneDrive 等同步目录内");
            else if (IsNetworkPath(root)) sb.Append(" / 注意：位于网络路径");
        }
        catch (Exception ex)
        {
            sb.Append("读取失败(").Append(ex.GetType().Name).Append(')');
        }

        return sb.ToString();
    }

    private static string DescribeDrive(string root)
    {
        try
        {
            var pathRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(pathRoot)) return "未知";

            var drive = new DriveInfo(pathRoot);
            if (!drive.IsReady) return $"{drive.Name}（不可用）";

            if (drive.DriveType == DriveType.Network) return $"{drive.Name} 网络驱动器（SQLite 会明显变慢）";
            if (drive.DriveType == DriveType.Removable) return $"{drive.Name} 可移动驱动器";

            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var totalGb = drive.TotalSize / (1024.0 * 1024 * 1024);
            return $"{drive.Name} 可用 {freeGb:F1}GB / 共 {totalGb:F0}GB";
        }
        catch (Exception)
        {
            return "未知";
        }
    }

    private static bool IsUnderSyncFolder(string path)
    {
        foreach (var variable in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            var folder = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(folder)) continue;
            if (path.StartsWith(folder!, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsNetworkPath(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal);

    // ---------------- WebView2 ----------------

    private static string DescribeWebView()
    {
        try
        {
            var version = WebViewRuntimeProbe.IsAvailable ? WebViewRuntimeProbe.Version : null;
            return Prefix + "WebView2=" + (string.IsNullOrWhiteSpace(version) ? "未安装（正文退化为纯文本渲染）" : version);
        }
        catch (Exception ex)
        {
            return Prefix + "WebView2=探测失败(" + ex.GetType().Name + ")";
        }
    }

    // ---------------- 辅助 ----------------

    private static string ToGb(ulong bytes)
        => (bytes / (1024.0 * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture);

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalMinutes < 1) return "不到 1 分钟（冷缓存）";
        if (uptime.TotalHours < 1) return $"{(int)uptime.TotalMinutes} 分钟（缓存多半是冷的）";
        return $"{uptime.TotalHours:F1} 小时";
    }

    private const int SmRemoteSession = 0x1000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailablePhys;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
