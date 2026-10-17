namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>
/// Persists structured log entries to the SQLite system-event log (spec §7.2).
/// A no-op implementation is wired in Tranche 0 (Application/Logging); the
/// SQLite-backed implementation lands in Tranche 1 alongside the schema.
/// </summary>
public interface ISystemEventWriter
{
    Task WriteAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default);
}
