using System.Text.RegularExpressions;
using ZungMail.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ZungMail.Tests;

/// <summary>
/// 日志并发写入：启动早期 AppLog 与宿主各持一个 provider，指向同一个 app-yyyyMMdd.log。
/// 两个 FileStream 各写各的会把行截断甚至互相覆盖，诊断日志必须先保证自己不坏。
/// </summary>
public sealed class FileLoggerConcurrencyTests
{
    private static readonly Regex CompleteLine =
        new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[Information\] [AB] - [AB]-\d+-END$",
            RegexOptions.Compiled);

    [Fact]
    public void TwoProvidersOnSameDirectoryNeverTearLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zungmail-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            WriteConcurrently(directory);

            var path = Path.Combine(directory, $"app-{DateTime.Now:yyyyMMdd}.log");
            var lines = File.ReadAllLines(path);

            Assert.Equal(600, lines.Length);
            Assert.All(lines, line => Assert.Matches(CompleteLine, line));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>两个 provider 同时写 300 行；返回时都已释放，文件句柄关闭。</summary>
    private static void WriteConcurrently(string directory)
    {
        using var first = new FileLoggerProvider(directory);
        using var second = new FileLoggerProvider(directory);
        var loggerA = first.CreateLogger("A");
        var loggerB = second.CreateLogger("B");

        Parallel.For(0, 300, i =>
        {
            loggerA.LogInformation("A-{Index}-END", i);
            loggerB.LogInformation("B-{Index}-END", i);
        });
    }

    [Fact]
    public void LastProviderDisposeClosesTheFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zungmail-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var first = new FileLoggerProvider(directory);
            var second = new FileLoggerProvider(directory);

            first.CreateLogger("A").LogInformation("A-1-END");
            first.Dispose();

            // 另一个 provider 还在写：文件必须仍是可写的
            second.CreateLogger("B").LogInformation("B-1-END");
            second.Dispose();

            // 两个都释放后句柄应关闭，文件可以被独占打开
            var path = Path.Combine(directory, $"app-{DateTime.Now:yyyyMMdd}.log");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(stream.Length > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
