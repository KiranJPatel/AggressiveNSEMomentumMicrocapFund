using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Logging;

/// <summary>
/// Tranche-0 placeholder for <see cref="ISystemEventWriter"/>, used before
/// the SQLite schema/wrapper existed. As of Tranche 1, the composition
/// root wires <c>TradingPlatform.Data.Repositories.SqliteSystemEventWriter</c>
/// instead — this type is kept only as a lightweight no-op for isolated
/// tests that don't want a real database.
/// </summary>
public sealed class NoOpSystemEventWriter : ISystemEventWriter
{
    public Task WriteAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
