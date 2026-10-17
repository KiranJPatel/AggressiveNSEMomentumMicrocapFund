using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

/// <summary>
/// Real SQLite-backed implementation of ISystemEventWriter (spec §7.2),
/// replacing Application/Logging/NoOpSystemEventWriter now that the schema
/// and SQLite wrapper exist (Tranche 1). DI registration for this swap
/// lives in the composition root (Application/Program.cs).
/// </summary>
public sealed class SqliteSystemEventWriter : ISystemEventWriter
{
    private readonly SqliteConnection _connection;

    public SqliteSystemEventWriter(SqliteConnection connection) => _connection = connection;

    public Task WriteAsync(SystemEvent systemEvent, CancellationToken cancellationToken = default)
    {
        _connection.ExecuteInsert(
            """
            INSERT INTO SystemEvents (Level, Category, Message, Exception, CorrelationId, Timestamp)
            VALUES (?,?,?,?,?,?);
            """,
            systemEvent.Level, systemEvent.Category, systemEvent.Message,
            systemEvent.Exception, systemEvent.CorrelationId, systemEvent.Timestamp);

        return Task.CompletedTask;
    }
}
