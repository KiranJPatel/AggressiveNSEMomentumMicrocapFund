using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Logging;

/// <summary>
/// Bridges Microsoft.Extensions.Logging into <see cref="ISystemEventWriter"/>
/// so every log entry also reaches the SQLite system-event sink (spec §7.2),
/// in addition to console + rolling file. Fire-and-forget by design: logging
/// must never block or throw into application code.
/// </summary>
public sealed class SystemEventLoggerProvider : ILoggerProvider
{
    private readonly ISystemEventWriter _writer;
    private readonly LogLevel _minimumLevel;

    public SystemEventLoggerProvider(ISystemEventWriter writer, LogLevel minimumLevel = LogLevel.Information)
    {
        _writer = writer;
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName) => new SystemEventLogger(categoryName, this);

    internal void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        if (level < _minimumLevel) return;

        var evt = new SystemEvent
        {
            Level = level.ToString(),
            Category = categoryName,
            Message = message,
            Exception = exception?.ToString(),
            Timestamp = DateTimeOffset.UtcNow
        };

        _ = _writer.WriteAsync(evt).ContinueWith(
            t => Console.Error.WriteLine($"[SystemEventLoggerProvider] failed to persist log event: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    public void Dispose() { }

    private sealed class SystemEventLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly SystemEventLoggerProvider _provider;

        public SystemEventLogger(string categoryName, SystemEventLoggerProvider provider)
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
