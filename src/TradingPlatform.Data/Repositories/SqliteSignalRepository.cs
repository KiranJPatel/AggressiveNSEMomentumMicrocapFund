using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

public sealed class SqliteSignalRepository : ISignalRepository
{
    private readonly SqliteConnection _connection;

    public SqliteSignalRepository(SqliteConnection connection) => _connection = connection;

    public Task<long> InsertAsync(Signal signal, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var id = _connection.ExecuteInsert(
            """
            INSERT INTO Signals
                (Symbol, GeneratedAt, Stage, PivotPrice, SuggestedEntry, SuggestedStop, RiskPerShare,
                 SuggestedPositionQty, SignalToOrderLag, StrategyVersion, ConfigurationVersion, BindingConstraints, CreatedAt)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?);
            """,
            signal.Symbol, signal.GeneratedAt, signal.Stage, signal.PivotPrice, signal.SuggestedEntry,
            signal.SuggestedStop, signal.RiskPerShare, signal.SuggestedPositionQty, signal.SignalToOrderLag,
            signal.StrategyVersion, signal.ConfigurationVersion, signal.BindingConstraints, now);
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<Signal>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query(
            "SELECT * FROM Signals ORDER BY GeneratedAt DESC LIMIT ?;", Map, limit);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Signal>> GetBySymbolAsync(string symbol, int limit, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query(
            "SELECT * FROM Signals WHERE Symbol = ? ORDER BY GeneratedAt DESC LIMIT ?;", Map, symbol, limit);
        return Task.FromResult(result);
    }

    private static Signal Map(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        Symbol = row.GetString("Symbol"),
        GeneratedAt = row.GetDateTimeOffset("GeneratedAt"),
        Stage = row.GetEnum<PyramidStage>("Stage"),
        PivotPrice = row.GetDecimal("PivotPrice"),
        SuggestedEntry = row.GetDecimal("SuggestedEntry"),
        SuggestedStop = row.GetDecimal("SuggestedStop"),
        RiskPerShare = row.GetDecimal("RiskPerShare"),
        SuggestedPositionQty = row.GetDecimal("SuggestedPositionQty"),
        SignalToOrderLag = row.GetEnum<SignalToOrderLag>("SignalToOrderLag"),
        StrategyVersion = row.GetString("StrategyVersion"),
        ConfigurationVersion = row.GetString("ConfigurationVersion"),
        BindingConstraints = row.GetNullableString("BindingConstraints"),
        CreatedAt = row.GetDateTimeOffset("CreatedAt"),
    };
}
