using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

/// <summary>Immutable completed-trade journal (spec §7.2) — see ITradeRepository's remarks on the live-vs-backtest split.</summary>
public sealed class SqliteTradeRepository : ITradeRepository
{
    private readonly SqliteConnection _connection;

    public SqliteTradeRepository(SqliteConnection connection) => _connection = connection;

    public Task<long> InsertAsync(Trade trade, IReadOnlyList<TradeLeg> legs, CancellationToken cancellationToken = default)
    {
        using var tx = _connection.BeginTransaction();

        var tradeId = _connection.ExecuteInsert(
            """
            INSERT INTO Trades
                (Symbol, SignalId, EntryPrice, InitialStop, InitialRiskAmount, ExitPrice, ExitReason,
                 RealizedPnl, RMultiple, MaximumFavorableExcursion, MaximumAdverseExcursion,
                 RegimeScoreAtEntry, StockScoreAtEntry, RiskAtEntryPercent, StrategyVersion,
                 ConfigurationVersion, EntryAt, ExitAt)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);
            """,
            trade.Symbol, trade.SignalId, trade.EntryPrice, trade.InitialStop, trade.InitialRiskAmount,
            trade.ExitPrice, trade.ExitReason, trade.RealizedPnl, trade.RMultiple,
            trade.MaximumFavorableExcursion, trade.MaximumAdverseExcursion, trade.RegimeScoreAtEntry,
            trade.StockScoreAtEntry, trade.RiskAtEntryPercent, trade.StrategyVersion,
            trade.ConfigurationVersion, trade.EntryAt, trade.ExitAt);

        foreach (var leg in legs)
        {
            _connection.ExecuteInsert(
                "INSERT INTO TradeLegs (TradeId, Stage, Quantity, Price, FilledAt) VALUES (?,?,?,?,?);",
                tradeId, leg.Stage, leg.Quantity, leg.Price, leg.FilledAt);
        }

        tx.Commit();
        return Task.FromResult(tradeId);
    }

    public Task<IReadOnlyList<Trade>> GetBySymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query("SELECT * FROM Trades WHERE Symbol = ? ORDER BY EntryAt;", MapTrade, symbol);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<TradeLeg>> GetLegsAsync(long tradeId, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query("SELECT * FROM TradeLegs WHERE TradeId = ? ORDER BY FilledAt;", MapLeg, tradeId);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Trade>> GetLiveTradesAsync(CancellationToken cancellationToken = default)
    {
        var result = _connection.Query(
            "SELECT * FROM Trades WHERE Id NOT IN (SELECT TradeId FROM BacktestTrades) ORDER BY EntryAt;",
            MapTrade);
        return Task.FromResult(result);
    }

    private static Trade MapTrade(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        Symbol = row.GetString("Symbol"),
        SignalId = row.GetNullableInt64("SignalId"),
        EntryPrice = row.GetDecimal("EntryPrice"),
        InitialStop = row.GetDecimal("InitialStop"),
        InitialRiskAmount = row.GetDecimal("InitialRiskAmount"),
        ExitPrice = row.GetDecimal("ExitPrice"),
        ExitReason = row.GetEnum<ExitReason>("ExitReason"),
        RealizedPnl = row.GetDecimal("RealizedPnl"),
        RMultiple = row.GetDecimal("RMultiple"),
        MaximumFavorableExcursion = row.GetDecimal("MaximumFavorableExcursion"),
        MaximumAdverseExcursion = row.GetDecimal("MaximumAdverseExcursion"),
        RegimeScoreAtEntry = row.GetInt32("RegimeScoreAtEntry"),
        StockScoreAtEntry = row.GetDecimal("StockScoreAtEntry"),
        RiskAtEntryPercent = row.GetDecimal("RiskAtEntryPercent"),
        StrategyVersion = row.GetString("StrategyVersion"),
        ConfigurationVersion = row.GetString("ConfigurationVersion"),
        EntryAt = row.GetDateTimeOffset("EntryAt"),
        ExitAt = row.GetDateTimeOffset("ExitAt"),
    };

    private static TradeLeg MapLeg(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        TradeId = row.GetInt64("TradeId"),
        Stage = row.GetEnum<PyramidStage>("Stage"),
        Quantity = row.GetDecimal("Quantity"),
        Price = row.GetDecimal("Price"),
        FilledAt = row.GetDateTimeOffset("FilledAt"),
    };
}
