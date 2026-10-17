using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TradingPlatform.Application.Logging;

/// <summary>
/// Hand-rolled rolling-file logger provider. Serilog (and its file/console
/// sinks) is a NuGet package and this environment has no NuGet access (see
/// docs/gap-log.md) — this reproduces the two behaviors the spec actually
/// needs from it: (1) daily rolling log files under the configured logs
/// directory, (2) structured single-line entries with timestamp/level/
/// category/message/exception. Secrets are never logged — callers are
/// responsible for not passing them in (see NEVER log secrets/tokens, spec §7.2).
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDirectory;
    private readonly LogLevel _minimumLevel;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private StreamWriter? _writer;
    private DateOnly _currentFileDate;

    public RollingFileLoggerProvider(string logsDirectory, LogLevel minimumLevel = LogLevel.Debug)
    {
        _logsDirectory = logsDirectory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(_logsDirectory);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(name, this));

    internal void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        if (level < _minimumLevel) return;

        lock (_writeLock)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (_writer is null || today != _currentFileDate)
            {
                _writer?.Flush();
                _writer?.Dispose();
                var path = Path.Combine(_logsDirectory, $"tradingplatform-{today:yyyy-MM-dd}.log");
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
                {
                    AutoFlush = true
                };
                _currentFileDate = today;
            }

            var line = $"{DateTimeOffset.UtcNow:O} [{level}] {categoryName}: {message}";
            if (exception is not null) line += Environment.NewLine + exception;
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        _writer?.Flush();
        _writer?.Dispose();
    }

    private sealed class RollingFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly RollingFileLoggerProvider _provider;

        public RollingFileLogger(string categoryName, RollingFileLoggerProvider provider)
        {
            _categoryName = categoryName;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _provider.Write(_categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}
