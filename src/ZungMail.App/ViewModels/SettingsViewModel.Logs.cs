using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace ZungMail.App.ViewModels;

/// <summary>
/// 设置 → 通用 →「诊断与日志」：运行日志的保留天数（1–7 天，默认 7）、当前占用与手动清理。
/// <para>
/// 真正的自动清理在 <see cref="ZungMail.App.Services.LogRetentionScheduler"/> 里按小时执行，
/// 这里只是让用户能看到并主动清一次，避免 logs 目录随使用时间悄悄涨上去。
/// </para>
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>可选保留天数：都 ≤7 天，符合「日志最多保存在本地 7 天」的约定。</summary>
    private static readonly int[] LogRetentionDayValues = { 1, 3, 5, 7 };

    public IReadOnlyList<string> LogRetentionOptions { get; } =
        LogRetentionDayValues.Select(days => days + " 天").ToList();

    /// <summary>当前日志目录占用（文件数 / 大小 / 最早一天），读取失败时给出原因。</summary>
    [ObservableProperty] private string _logUsageText = "";

    /// <summary>保留天数下拉的选中项；写回设置后立刻刷新占用说明。</summary>
    public int LogRetentionIndex
    {
        get => NearestLogRetentionIndex(Settings.LogRetentionDays);
        set
        {
            if (value < 0 || value >= LogRetentionDayValues.Length) return;

            var days = LogRetentionDayValues[value];
            if (days == AppSettings.EffectiveLogRetentionDays(Settings.LogRetentionDays)) return;

            Settings.LogRetentionDays = days;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LogRetentionHint));
            RefreshLogUsage();
        }
    }

    /// <summary>下拉下面的说明：讲清「最多 7 天」与「超期自动删」。</summary>
    public string LogRetentionHint
        => $"日志只保留最近 {AppSettings.EffectiveLogRetentionDays(Settings.LogRetentionDays)} 天（最多 7 天），" +
           "超期文件在程序运行期间自动删除；只清理 logs 目录下的 app-日期.log，不影响邮件与设置。";

    /// <summary>按当前保留天数清一次超期日志；没有超期文件时给出明确提示。</summary>
    [RelayCommand]
    private void CleanLogs()
    {
        var keepDays = AppSettings.EffectiveLogRetentionDays(Settings.LogRetentionDays);
        var result = LogRetention.Run(
            _paths.LogDirectory,
            keepDays,
            DateTime.Now,
            (path, ex) => _logger.LogWarning(ex, "手动清理超期日志失败：{Path}", path));

        RefreshLogUsage();

        StatusText = result.DeletedFiles == 0
            ? $"没有超过 {keepDays} 天的日志需要清理。"
            : result.FailedFiles > 0
                ? $"已清理 {result.DeletedFiles} 个超期日志（释放 {FormatLogSize(result.DeletedBytes)}），" +
                  $"另有 {result.FailedFiles} 个被占用，稍后自动重试。"
                : $"已清理 {result.DeletedFiles} 个超期日志，释放 {FormatLogSize(result.DeletedBytes)}。";
    }

    /// <summary>刷新日志目录占用：文件数按 <see cref="LogRetention.SearchPattern"/> 统计，不递归。</summary>
    public void RefreshLogUsage()
    {
        try
        {
            var directory = _paths.LogDirectory;
            if (!Directory.Exists(directory))
            {
                LogUsageText = "还没有日志文件。";
                return;
            }

            var files = new DirectoryInfo(directory)
                .GetFiles(LogRetention.SearchPattern, SearchOption.TopDirectoryOnly)
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .ToList();

            if (files.Count == 0)
            {
                LogUsageText = "还没有日志文件。";
                return;
            }

            var bytes = files.Sum(file => file.Length);
            var earliest = LogRetention.TryParseDate(files[0].Name, out var date)
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "未知";

            LogUsageText = $"当前 {files.Count} 个日志文件 · 共 {FormatLogSize(bytes)} · 最早 {earliest}";
        }
        catch (Exception ex)
        {
            LogUsageText = "读取日志目录失败：" + ex.Message;
        }
    }

    /// <summary>把设置里的天数对齐到下拉档位：取最接近的一档（异常值先夹到 1–7）。</summary>
    private static int NearestLogRetentionIndex(int days)
    {
        var target = AppSettings.EffectiveLogRetentionDays(days);
        var best = 0;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < LogRetentionDayValues.Length; i++)
        {
            var distance = Math.Abs(LogRetentionDayValues[i] - target);
            if (distance >= bestDistance) continue;

            best = i;
            bestDistance = distance;
        }

        return best;
    }

    private static string FormatLogSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0} KB";
        return $"{kb / 1024.0:0.0} MB";
    }
}
