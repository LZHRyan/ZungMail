using ZungMail.App.Services;
using ZungMail.Core.Domain;
using Xunit;

namespace ZungMail.UiTests;

/// <summary>
/// 启动环境快照：每行都要带统一前缀，且不能因为探测失败而抛异常
/// （这些信息是给「这台机器为什么慢」用的，缺项可以留白，绝不能挡住启动）。
/// </summary>
public sealed class StartupEnvironmentTests
{
    [Fact]
    public void Describe_ReturnsPrefixedLinesForEverySection()
    {
        var lines = StartupEnvironment.Describe();

        Assert.Equal(6, lines.Count);
        Assert.All(lines, line => Assert.StartsWith("启动环境：", line));
        Assert.Contains(lines, line => line.StartsWith("启动环境：系统="));
        Assert.Contains(lines, line => line.StartsWith("启动环境：CPU="));
        Assert.Contains(lines, line => line.StartsWith("启动环境：图形="));
        Assert.Contains(lines, line => line.StartsWith("启动环境：进程="));
        Assert.Contains(lines, line => line.StartsWith("启动环境：数据目录="));
        Assert.Contains(lines, line => line.StartsWith("启动环境：WebView2="));
    }

    [Fact]
    public void Describe_ReportsBitnessAndUptimeOnSystemLine()
    {
        var systemLine = StartupEnvironment.Describe()[0];

        Assert.Contains(Environment.Is64BitOperatingSystem ? "64 位" : "32 位", systemLine);
        Assert.Contains("已开机", systemLine);
    }

    [Fact]
    public void DescribeSettings_ReflectsLatencyRelevantOptions()
    {
        var settings = new AppSettings
        {
            SyncOnLaunch = true,
            SyncIntervalMinutes = 5,
            ListPageSize = 100,
            ConversationDefault = false,
            AutoCleanupEnabled = true,
            ClearBodyCacheOnExit = false
        };

        var line = StartupEnvironment.DescribeSettings(settings);

        Assert.StartsWith("启动环境：设置=", line);
        Assert.Contains("启动即同步 是", line);
        Assert.Contains("同步间隔 5 分钟", line);
        Assert.Contains("列表每页 100 封", line);
        Assert.Contains("自动清理 开", line);
        Assert.Contains("退出清正文缓存 否", line);
    }

    [Fact]
    public void DescribeSettings_ClampsOutOfRangePageSize()
    {
        var settings = new AppSettings { ListPageSize = 0 };

        Assert.Contains("列表每页 10 封", StartupEnvironment.DescribeSettings(settings));
    }
}
