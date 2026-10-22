using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZungMail.Core.Domain;
using ZungMail.Infrastructure.Logging;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 日志保留期：<c>logs\app-日期.log</c> 只保留最近 7 天（上限），超期自动删除。
/// 覆盖边界日、非法文件名、目录不存在、文件被占用四种情况——清理绝不能抛异常影响主流程。
/// </summary>
public sealed class LogRetentionTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0);

    [Theory]
    [InlineData(0, 7)]      // 未设置 / 配置损坏 → 回到默认 7 天，不能误删历史日志
    [InlineData(-30, 7)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(7, 7)]
    [InlineData(8, 7)]
    [InlineData(365, 7)]
    public void Normalize_ClampsToSevenDayCap(int input, int expected)
        => Assert.Equal(expected, LogRetentionPolicy.Normalize(input));

    [Fact]
    public void EarliestKeptDate_IsTodayMinusKeepDaysMinusOne()
    {
        var today = new DateOnly(2026, 10, 21);

        Assert.Equal(new DateOnly(2026, 10, 15), LogRetentionPolicy.EarliestKeptDate(today, 7));
        Assert.Equal(today, LogRetentionPolicy.EarliestKeptDate(today, 1));
    }

    [Fact]
    public void IsExpired_OnlyFilesBeforeTheWindowAreExpired()
    {
        var today = new DateOnly(2026, 10, 21);

        Assert.True(LogRetentionPolicy.IsExpired(new DateOnly(2026, 10, 14), today, 7));
        Assert.False(LogRetentionPolicy.IsExpired(new DateOnly(2026, 10, 15), today, 7));
        Assert.False(LogRetentionPolicy.IsExpired(today, today, 7));
        // 系统时间被往回调时，「未来日期」的文件也不算超期
        Assert.False(LogRetentionPolicy.IsExpired(new DateOnly(2026, 12, 1), today, 7));
    }

    [Fact]
    public void AppSettings_DefaultsToSevenDays()
    {
        var settings = new AppSettings();

        Assert.Equal(7, settings.LogRetentionDays);
        Assert.Equal(7, AppSettings.EffectiveLogRetentionDays(0));
        Assert.Equal(7, AppSettings.EffectiveLogRetentionDays(99));
    }

    [Theory]
    [InlineData("app-20261021.log", true)]
    [InlineData("app-20261021.LOG", true)]
    [InlineData("app-2026-10-21.log", false)]
    [InlineData("app-20261321.log", false)]
    [InlineData("app-20261021.log.bak", false)]
    [InlineData("app-20261021.1.log", false)]
    [InlineData("feedback.log", false)]
    [InlineData("app-abc.log", false)]
    [InlineData("", false)]
    public void TryParseDate_OnlyAcceptsDailyLogNames(string name, bool expected)
        => Assert.Equal(expected, LogRetention.TryParseDate(name, out _));

    [Fact]
    public void Run_DeletesExpiredDailyLogsAndLeavesEverythingElse()
    {
        var directory = CreateTempDirectory();
        try
        {
            Write(directory, "app-20261010.log", 100);      // 超期
            Write(directory, "app-20261014.log", 200);      // 超期（窗口从 10-15 起）
            Write(directory, "app-20261015.log", 300);      // 边界：保留
            Write(directory, "app-20261021.log", 400);      // 今天
            Write(directory, "feedback.log", 10);           // 不是日志滚动文件
            Write(directory, "app-20261010.log.bak", 10);   // 备份文件
            Write(directory, "notes.txt", 10);

            var result = LogRetention.Run(directory, 7, Now);

            Assert.Equal(2, result.DeletedFiles);
            Assert.Equal(300, result.DeletedBytes);
            Assert.Equal(0, result.FailedFiles);

            var remaining = Directory.GetFiles(directory).Select(Path.GetFileName).ToList();
            Assert.DoesNotContain("app-20261010.log", remaining);
            Assert.DoesNotContain("app-20261014.log", remaining);
            Assert.Contains("app-20261015.log", remaining);
            Assert.Contains("app-20261021.log", remaining);
            Assert.Contains("feedback.log", remaining);
            Assert.Contains("app-20261010.log.bak", remaining);
            Assert.Contains("notes.txt", remaining);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Run_IsIdempotent()
    {
        var directory = CreateTempDirectory();
        try
        {
            Write(directory, "app-20261001.log", 64);
            Write(directory, "app-20261020.log", 64);

            Assert.Equal(1, LogRetention.Run(directory, 7, Now).DeletedFiles);

            var second = LogRetention.Run(directory, 7, Now);
            Assert.Equal(0, second.DeletedFiles);
            Assert.Equal(0, second.DeletedBytes);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Run_KeepDaysFromSettingsIsClampedToSeven()
    {
        var directory = CreateTempDirectory();
        try
        {
            Write(directory, "app-20261014.log", 10);   // 保留 7 天时超期
            Write(directory, "app-20261015.log", 10);   // 保留 7 天时仍在窗口内

            // 传入 365 天也必须按 7 天执行，否则「最多保存 7 天」会被配置绕过
            var result = LogRetention.Run(directory, 365, Now);

            Assert.Equal(1, result.DeletedFiles);
            Assert.True(File.Exists(Path.Combine(directory, "app-20261015.log")));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Run_MissingDirectoryReturnsEmptyResult()
    {
        var missing = Path.Combine(Path.GetTempPath(), "mc-log-retention-none-" + Guid.NewGuid().ToString("N"));

        var result = LogRetention.Run(missing, 7, Now);

        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(0, result.FailedFiles);
        Assert.False(result.AnyDeleted);
    }

    [Fact]
    public void Run_LockedFileIsCountedAsFailureAndReported()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Write(directory, "app-20261001.log", 32);
            var warnings = new List<string>();

            // 独占打开（其它实例正在写日志）：删除会失败，但必须只记一次告警并继续
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = LogRetention.Run(directory, 7, Now, (failedPath, _) => warnings.Add(failedPath));

                Assert.Equal(0, result.DeletedFiles);
                Assert.Equal(1, result.FailedFiles);
                Assert.Single(warnings);
            }

            Assert.True(File.Exists(path));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mc-log-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Write(string directory, string name, int bytes)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // 清理测试产物失败不影响结论
        }
    }
}
