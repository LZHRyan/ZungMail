using System.Globalization;
using System.Text;
using ZungMail.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace ZungMail.Infrastructure.Logging;

/// <summary>
/// 极简文件日志：写入 &lt;LogDirectory&gt;\app-yyyyMMdd.log，线程安全、按天滚动，
/// 单文件超过 5MB 时截断重开（NFR-C3：同步与发送异常均可定位）。
/// <para>
/// 同一目录的写句柄在进程内共享（<see cref="FileLogSink"/>）：
/// 启动早期 <c>AppLog</c> 与宿主各持一个 provider，若两个 FileStream 各写各的，
/// 同一行会被另一次写入截断甚至覆盖（诊断日志自己先坏掉，就没什么可诊断的了）。
/// </para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogSink _sink;
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(IAppPaths paths) : this(paths.LogDirectory, LogLevel.Information)
    {
    }

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        _sink = FileLogSink.For(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>写句柄由进程内共享的 sink 持有；最后一个 provider 释放时才关文件。</summary>
    public void Dispose() => _sink.Release();

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimumLevel;

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .Append(category).Append(" - ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        _sink.WriteLine(line.ToString());
    }
}

/// <summary>
/// 单个日志目录的实际写入者：一把锁 + 一个 StreamWriter，按天滚动、超限截断。
/// 进程内按目录共享，保证并发写不会把行拆开。
/// </summary>
internal sealed class FileLogSink
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileLogSink> Sinks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _directory;
    private readonly object _gate = new();

    private int _references;
    private StreamWriter? _writer;
    private string? _currentDate;
    private string? _currentPath;

    private FileLogSink(string directory) => _directory = directory;

    public static FileLogSink For(string directory)
    {
        var sink = Sinks.GetOrAdd(Path.GetFullPath(directory), path => new FileLogSink(path));
        sink.AddReference();
        return sink;
    }

    private void AddReference() => Interlocked.Increment(ref _references);

    /// <summary>引用计数归零（所有 provider 都释放）时才关闭文件句柄。</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _references) > 0) return;

        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            _currentDate = null;
            _currentPath = null;
        }
    }

    public void WriteLine(string text)
    {
        lock (_gate)
        {
            try
            {
                EnsureWriter();
                _writer!.WriteLine(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写入失败绝不能影响主流程：多实例共用日志文件、磁盘满等情况直接丢弃本条
                try { _writer?.Dispose(); }
                catch (Exception) { /* 忽略释放失败 */ }
                _writer = null;
            }
        }
    }

    private void EnsureWriter()
    {
        var date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_writer is null || _currentDate != date)
        {
            _writer?.Dispose();
            _currentPath = Path.Combine(_directory, $"app-{date}.log");
            _writer = Open(append: true);
            _currentDate = date;
            return;
        }

        if (_writer.BaseStream.Length > MaxBytes)
        {
            _writer.Dispose();
            _writer = Open(append: false); // 同日文件超限：截断重开
        }
    }

    private StreamWriter Open(bool append)
    {
        // 允许多实例并发追加，避免第二个实例因日志文件被占用而启动失败
        var stream = new FileStream(_currentPath!, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
    }
}

/// <summary>把 ILogger 调用转发到 FileLoggerProvider。</summary>
internal sealed class FileLogger : ILogger
{
    private readonly FileLoggerProvider _provider;
    private readonly string _category;

    public FileLogger(FileLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        _provider.Write(logLevel, _category, message, exception);
    }
}
