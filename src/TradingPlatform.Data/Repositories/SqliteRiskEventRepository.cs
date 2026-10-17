using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteRiskEventRepository : IRiskEventRepository
{
    private readonly SqliteConnection _connection;

    public SqliteRiskEventRepository(SqliteConnection connection) => _connection = connection;

    public Task InsertAsync(RiskEvent riskEvent, CancellationToken cancellationToken = default)
    {
        _connection.ExecuteInsert(
            "INSERT INTO RiskEvents (EventType, Description, Symbol, Timestamp, CorrelationId) VALUES (?,?,?,?,?);",
            riskEvent.EventType, riskEvent.Description, riskEvent.Symbol, riskEvent.Timestamp, riskEvent.CorrelationId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RiskEvent>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query("SELECT * FROM RiskEvents ORDER BY Timestamp DESC LIMIT ?;", Map, limit);
        return Task.FromResult(result);
    }

    public Task<RiskEvent?> GetMostRecentByTypeAsync(string eventType, CancellationToken cancellationToken = default)
    {
        var result = _connection.QuerySingleOrDefault(
            "SELECT * FROM RiskEvents WHERE EventType = ? ORDER BY Timestamp DESC LIMIT 1;", Map, eventType);
        return Task.FromResult(result);
    }

    private static RiskEvent Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        EventType = row.GetString("EventType"),
        Description = row.GetString("Description"),
        Symbol = row.GetNullableString("Symbol"),
        Timestamp = row.GetDateTimeOffset("Timestamp"),
        CorrelationId = row.GetString("CorrelationId"),
    };
}
