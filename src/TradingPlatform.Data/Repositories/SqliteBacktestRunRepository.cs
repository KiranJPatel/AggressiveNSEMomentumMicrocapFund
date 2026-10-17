using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Data.Sqlite;

namespace TradingPlatform.Data.Repositories;

/// <summary>BacktestRuns + BacktestTrades link table + the BacktestRunId-scoped BacktestSnapshots/BacktestDrawdownEvents (spec §7.6) — see IBacktestRunRepository's remarks and SchemaDefinition's remarks on why these are separate from the live tables.</summary>
public sealed class SqliteBacktestRunRepository : IBacktestRunRepository
{
    private readonly SqliteConnection _connection;

    public SqliteBacktestRunRepository(SqliteConnection connection) => _connection = connection;

    public Task<long> InsertStartedAsync(BacktestRun run, CancellationToken cancellationToken = default)
    {
        var id = _connection.ExecuteInsert(
            """
            INSERT INTO BacktestRuns
                (StrategyVersion, ConfigurationHash, DataVersion, StartDate, EndDate, Status, SurvivorshipBiased,
                 FinalCapital, Cagr, MaxDrawdownPercent, Sharpe, Sortino, WinRatePercent, ProfitFactor,
                 ExpectancyR, TradeCount, StartedAt, CompletedAt)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);
            """,
            run.StrategyVersion, run.ConfigurationHash, run.DataVersion, run.StartDate, run.EndDate,
            BacktestRunStatus.Running, run.SurvivorshipBiased, run.FinalCapital, run.Cagr,
            run.MaxDrawdownPercent, run.Sharpe, run.Sortino, run.WinRatePercent, run.ProfitFactor,
            run.ExpectancyR, run.TradeCount, run.StartedAt, run.CompletedAt);
        return Task.FromResult(id);
    }

    public Task UpdateCompletedAsync(BacktestRun run, CancellationToken cancellationToken = default)
    {
        _connection.Execute(
            """
            UPDATE BacktestRuns SET Status = ?, SurvivorshipBiased = ?, FinalCapital = ?, Cagr = ?,
                MaxDrawdownPercent = ?, Sharpe = ?, Sortino = ?, WinRatePercent = ?, ProfitFactor = ?,
                ExpectancyR = ?, TradeCount = ?, CompletedAt = ?
            WHERE Id = ?;
            """,
            run.Status, run.SurvivorshipBiased, run.FinalCapital, run.Cagr, run.MaxDrawdownPercent,
            run.Sharpe, run.Sortino, run.WinRatePercent, run.ProfitFactor, run.ExpectancyR, run.TradeCount,
            run.CompletedAt, run.Id);
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(long id, DateTimeOffset failedAt, CancellationToken cancellationToken = default)
    {
        _connection.Execute(
            "UPDATE BacktestRuns SET Status = ?, CompletedAt = ? WHERE Id = ?;",
            BacktestRunStatus.Failed, failedAt, id);
        return Task.CompletedTask;
    }

    public Task LinkTradesAsync(long backtestRunId, IReadOnlyList<long> tradeIds, CancellationToken cancellationToken = default)
    {
        using var tx = _connection.BeginTransaction();
        foreach (var tradeId in tradeIds)
        {
            _connection.ExecuteInsert(
                "INSERT INTO BacktestTrades (BacktestRunId, TradeId) VALUES (?,?);", backtestRunId, tradeId);
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task InsertSnapshotsAsync(long backtestRunId, IReadOnlyList<PortfolioSnapshot> snapshots, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        using var tx = _connection.BeginTransaction();
        foreach (var s in snapshots)
        {
            _connection.ExecuteInsert(
                """
                INSERT INTO BacktestSnapshots
                    (BacktestRunId, AsOfDate, Nav, CashBalance, PeakNav, DrawdownPercent, PortfolioHeatPercent,
                     OpenPositionCount, Regime, CreatedAt)
                VALUES (?,?,?,?,?,?,?,?,?,?);
                """,
                backtestRunId, s.AsOfDate, s.Nav, s.CashBalance, s.PeakNav, s.DrawdownPercent,
                s.PortfolioHeatPercent, s.OpenPositionCount, s.Regime, now);
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task InsertDrawdownEventsAsync(long backtestRunId, IReadOnlyList<DrawdownEvent> drawdownEvents, CancellationToken cancellationToken = default)
    {
        using var tx = _connection.BeginTransaction();
        foreach (var e in drawdownEvents)
        {
            _connection.ExecuteInsert(
                """
                INSERT INTO BacktestDrawdownEvents
                    (BacktestRunId, DrawdownPercent, TierName, RiskReductionPercent, TradingShutdown, Timestamp)
                VALUES (?,?,?,?,?,?);
                """,
                backtestRunId, e.DrawdownPercent, e.TierName, e.RiskReductionPercent, e.TradingShutdown, e.Timestamp);
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BacktestRun>> GetRunsAsync(CancellationToken cancellationToken = default)
    {
        var result = _connection.Query("SELECT * FROM BacktestRuns ORDER BY StartedAt DESC;", MapRun);
        return Task.FromResult(result);
    }

    public Task<BacktestRun?> GetRunAsync(long id, CancellationToken cancellationToken = default)
    {
        var result = _connection.QuerySingleOrDefault("SELECT * FROM BacktestRuns WHERE Id = ?;", MapRun, id);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Trade>> GetTradesAsync(long backtestRunId, CancellationToken cancellationToken = default)
    {
        // t.* rather than * keeps the result shape identical to a plain
        // "SELECT * FROM Trades" (only Trades' own columns, none of
        // BacktestTrades'), so the same MapTrade below applies unchanged.
        var result = _connection.Query(
            """
            SELECT t.* FROM Trades t
            INNER JOIN BacktestTrades bt ON bt.TradeId = t.Id
            WHERE bt.BacktestRunId = ?
            ORDER BY t.EntryAt;
            """,
            MapTrade, backtestRunId);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<PortfolioSnapshot>> GetSnapshotsAsync(long backtestRunId, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query(
            "SELECT * FROM BacktestSnapshots WHERE BacktestRunId = ? ORDER BY AsOfDate;", MapSnapshot, backtestRunId);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<DrawdownEvent>> GetDrawdownEventsAsync(long backtestRunId, CancellationToken cancellationToken = default)
    {
        var result = _connection.Query(
            "SELECT * FROM BacktestDrawdownEvents WHERE BacktestRunId = ? ORDER BY Timestamp;", MapDrawdownEvent, backtestRunId);
        return Task.FromResult(result);
    }

    private static BacktestRun MapRun(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        StrategyVersion = row.GetString("StrategyVersion"),
        ConfigurationHash = row.GetString("ConfigurationHash"),
        DataVersion = row.GetString("DataVersion"),
        StartDate = row.GetDateOnly("StartDate"),
        EndDate = row.GetDateOnly("EndDate"),
        Status = row.GetEnum<BacktestRunStatus>("Status"),
        SurvivorshipBiased = row.GetBoolean("SurvivorshipBiased"),
        FinalCapital = row.GetNullableDecimal("FinalCapital"),
        Cagr = row.GetNullableDecimal("Cagr"),
        MaxDrawdownPercent = row.GetNullableDecimal("MaxDrawdownPercent"),
        Sharpe = row.GetNullableDecimal("Sharpe"),
        Sortino = row.GetNullableDecimal("Sortino"),
        WinRatePercent = row.GetNullableDecimal("WinRatePercent"),
        ProfitFactor = row.GetNullableDecimal("ProfitFactor"),
        ExpectancyR = row.GetNullableDecimal("ExpectancyR"),
        TradeCount = row.GetNullableInt32("TradeCount"),
        StartedAt = row.GetDateTimeOffset("StartedAt"),
        CompletedAt = row.GetNullableDateTimeOffset("CompletedAt"),
    };

    private static PortfolioSnapshot MapSnapshot(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        AsOfDate = row.GetDateOnly("AsOfDate"),
        Nav = row.GetDecimal("Nav"),
        CashBalance = row.GetDecimal("CashBalance"),
        PeakNav = row.GetDecimal("PeakNav"),
        DrawdownPercent = row.GetDecimal("DrawdownPercent"),
        PortfolioHeatPercent = row.GetDecimal("PortfolioHeatPercent"),
        OpenPositionCount = row.GetInt32("OpenPositionCount"),
        Regime = row.GetNullableEnum<RegimeClassification>("Regime"),
        CreatedAt = row.GetDateTimeOffset("CreatedAt"),
    };

    private static DrawdownEvent MapDrawdownEvent(SqliteRow row) => new()
    {
        Id = row.GetInt64("Id"),
        DrawdownPercent = row.GetDecimal("DrawdownPercent"),
        TierName = row.GetString("TierName"),
        RiskReductionPercent = row.GetDecimal("RiskReductionPercent"),
        TradingShutdown = row.GetBoolean("TradingShutdown"),
        Timestamp = row.GetDateTimeOffset("Timestamp"),
    };

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
}
