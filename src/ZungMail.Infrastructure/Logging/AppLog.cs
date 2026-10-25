using ZungMail.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace ZungMail.Infrastructure.Logging;

/// <summary>
/// 全局日志便捷入口：惰性初始化一个文件日志工厂。
/// 供 Mail/App 层在无法注入 ILogger 的位置（如静态异常处理）记录可定位的错误日志（NFR-C3）。
/// </summary>
public static class AppLog
{
    private static readonly Lazy<ILogger> Logger = new(() =>
    {
        ILoggerFactory factory = new SimpleLoggerFactory();
        factory.AddProvider(new FileLoggerProvider(new AppPaths()));
        return factory.CreateLogger("ZungMail");
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Info(string message) => Logger.Value.LogInformation("{Message}", message);

    public static void Warn(string message) => Logger.Value.LogWarning("{Message}", message);

    public static void Error(string message, Exception? exception = null)
        => Logger.Value.LogError(exception, "{Message}", message);
}

/// <summary>最小可用的 ILoggerFactory（Logging.Abstractions 未提供具体实现）。</summary>
internal sealed class SimpleLoggerFactory : ILoggerFactory
{
    private readonly List<ILoggerProvider> _providers = new();

    public void AddProvider(ILoggerProvider provider)
    {
        lock (_providers)
        {
            _providers.Add(provider);
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        ILoggerProvider[] snapshot;
        lock (_providers)
        {
            snapshot = _providers.ToArray();
        }
        if (snapshot.Length == 1) return snapshot[0].CreateLogger(categoryName);
        return new CompositeLogger(snapshot.Select(p => p.CreateLogger(categoryName)).ToList());
    }

    public void Dispose()
    {
        lock (_providers)
        {
            foreach (var provider in _providers) provider.Dispose();
            _providers.Clear();
        }
    }
}

/// <summary>多 provider 时的日志广播器。</summary>
internal sealed class CompositeLogger : ILogger
{
    private readonly IReadOnlyList<ILogger> _loggers;

    public CompositeLogger(IReadOnlyList<ILogger> loggers) => _loggers = loggers;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => _loggers.Any(l => l.IsEnabled(logLevel));

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        foreach (var logger in _loggers)
        {
            logger.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
