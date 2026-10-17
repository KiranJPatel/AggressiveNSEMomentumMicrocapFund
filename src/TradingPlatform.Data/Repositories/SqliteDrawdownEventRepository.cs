using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

/// <summary>Live/paper drawdown tier transitions — see IDrawdownEventRepository's remarks on the split from BacktestDrawdownEvents.</summary>
public sealed class SqliteDrawdownEventRepository : IDrawdownEventRepository
{
    private readonly SqliteConnection _connection;

    public SqliteDrawdownEventRepository(SqliteConnection connection) => _connection = connection;

    public Task InsertAsync(DrawdownEvent drawdownEvent, CancellationToken cancellationToken = default)
    {
        _connection.ExecuteInsert(
            """
            INSERT INTO DrawdownEvents (DrawdownPercent, TierName, RiskReductionPercent, TradingShutdown, Timestamp)
            VALUES (?,?,?,?,?);
            """,
            drawdownEvent.DrawdownPercent, drawdownEvent.TierName, drawdownEvent.RiskReductionPercent,
            drawdownEvent.TradingShutdown, drawdownEvent.Timestamp);
        return Task.CompletedTask;
    }

    public Task<DrawdownEvent?> GetMostRecentAsync(CancellationToken cancellationToken = default)
    {
        var result = _connection.QuerySingleOrDefault(
            "SELECT * FROM DrawdownEvents ORDER BY Timestamp DESC LIMIT 1;", Map);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<DrawdownEvent>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query("SELECT * FROM DrawdownEvents ORDER BY Timestamp DESC LIMIT ?;", Map, limit);
        return Task.FromResult(result);
    }

    private static DrawdownEvent Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        DrawdownPercent = row.GetDecimal("DrawdownPercent"),
        TierName = row.GetString("TierName"),
        RiskReductionPercent = row.GetDecimal("RiskReductionPercent"),
        TradingShutdown = row.GetBoolean("TradingShutdown"),
        Timestamp = row.GetDateTimeOffset("Timestamp"),
    };
}
