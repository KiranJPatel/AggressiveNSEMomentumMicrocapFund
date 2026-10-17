using TradingPlatform.Core.Interfaces;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteEngineStateRepository : IEngineStateRepository
{
    private readonly SqliteConnection _connection;

    public SqliteEngineStateRepository(SqliteConnection connection) => _connection = connection;

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var value = _connection.QuerySingleOrDefault(
            "SELECT Value FROM EngineState WHERE Key = ?;", row => row.GetString("Value"), key);
        return Task.FromResult(value);
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _connection.Execute(
            """
            INSERT INTO EngineState (Key, Value, UpdatedAt) VALUES (?,?,?)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value, UpdatedAt = excluded.UpdatedAt;
            """,
            key, value, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}
