using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Core.Interfaces;

/// <summary>
/// SQLite-backed persistence for the tables Tranche 1 deliberately deferred
/// (only Instrument/HistoricalBar/UniverseClassification/Surveillance were
/// built then — see docs/gap-log.md). Bundled in one file for the same
/// reason as ApiQueryServices.cs: each interface is a small, focused
/// read/write contract for one table group, and splitting nine of them
/// across nine files adds navigation overhead for no benefit. Every method
/// follows the same conventions already established by
/// SqliteInstrumentRepository/SqliteHistoricalBarRepository: async
/// signatures (even though the underlying hand-rolled SqliteConnection is
/// synchronous under the hood - Task.FromResult, same as Tranche 1), and
/// upsert-by-natural-key where the table has a real dedup key.
/// </summary>
public interface ISignalRepository
{
    Task<long> InsertAsync(Signal signal, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Signal>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Signal>> GetBySymbolAsync(string symbol, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Orders + their immutable OrderEvents state-transition log + Fills, kept together since they share one lifecycle (spec §6.3, §7.2).</summary>
public interface IOrderRepository
{
    Task<long> InsertAsync(Order order, CancellationToken cancellationToken = default);

    /// <summary>Updates Orders.State (and BrokerOrderId/RejectionReason if supplied) and appends one OrderEvents row in the same transaction — an order's state is never changed without a corresponding immutable event recording the transition.</summary>
    Task RecordStateTransitionAsync(long orderId, OrderState fromState, OrderState toState, string? brokerOrderId, string? rejectionReason, string? detail, DateTimeOffset timestamp, CancellationToken cancellationToken = default);

    Task<long> RecordFillAsync(Fill fill, CancellationToken cancellationToken = default);

    Task<Order?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Order>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrderEvent>> GetEventsAsync(long orderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Fill>> GetFillsAsync(long orderId, CancellationToken cancellationToken = default);
}

/// <summary>Local open/closed position store — distinct from IPositionService (Tranche 12), which is the BROKER-facing reconciliation contract; this is the locally-tracked state that gets reconciled against it (spec §6.4, §6.5's restart recovery).</summary>
public interface IPositionRepository
{
    /// <summary>Insert if Id == 0, otherwise update the existing row in place (stop trailing, stage additions). Returns the row's Id.</summary>
    Task<long> UpsertAsync(Position position, CancellationToken cancellationToken = default);
    Task<Position?> GetOpenBySymbolAsync(string symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Position>> GetOpenAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(long positionId, DateTimeOffset closedAt, ExitReason reason, CancellationToken cancellationToken = default);
}

/// <summary>Immutable completed-trade journal (spec §7.2) — Trades/TradeLegs are NEVER updated after insert, only ever read back.</summary>
public interface ITradeRepository
{
    /// <summary>Inserts the Trade and all of its legs (with TradeId back-filled) in one transaction. Returns the new Trade's Id.</summary>
    Task<long> InsertAsync(Trade trade, IReadOnlyList<TradeLeg> legs, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Trade>> GetBySymbolAsync(string symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TradeLeg>> GetLegsAsync(long tradeId, CancellationToken cancellationToken = default);

    /// <summary>Every Trade NOT linked to a BacktestRun via BacktestTrades — i.e. genuinely live/paper trades, for performance reporting (spec §7.5's GET /api/performance) that must never silently include historical backtest simulation output.</summary>
    Task<IReadOnlyList<Trade>> GetLiveTradesAsync(CancellationToken cancellationToken = default);
}

public interface IRiskEventRepository
{
    Task InsertAsync(RiskEvent riskEvent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RiskEvent>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Most recent event of a given EventType (e.g. "KillSwitchActivated", "KillSwitchCleared", "DailyLossLimitBreached") — the resolved approach for reconstructing current kill-switch/loss-limit status from the append-only log rather than a separate mutable state table (see SqliteRiskQueryService).</summary>
    Task<RiskEvent?> GetMostRecentByTypeAsync(string eventType, CancellationToken cancellationToken = default);
}

/// <summary>Live/paper drawdown circuit-breaker tier transitions (spec §4.5) — distinct from the BacktestRunId-scoped BacktestDrawdownEvents table (see SchemaDefinition remarks) which never touches this one.</summary>
public interface IDrawdownEventRepository
{
    Task InsertAsync(DrawdownEvent drawdownEvent, CancellationToken cancellationToken = default);
    Task<DrawdownEvent?> GetMostRecentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DrawdownEvent>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>Live portfolio EOD snapshots (spec §7.2) — distinct from BacktestSnapshots (see SchemaDefinition remarks).</summary>
public interface IPortfolioSnapshotRepository
{
    /// <summary>Upserts on the AsOfDate unique key — safe to call more than once for the same day (e.g. an intraday heat update followed by the real post-market snapshot).</summary>
    Task UpsertAsync(PortfolioSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<PortfolioSnapshot?> GetLatestAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PortfolioSnapshot>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

public interface IScannerResultRepository
{
    /// <summary>Persists one full scan cycle's ranked results (all rows share the same ScanTimestamp, spec §7.1).</summary>
    Task InsertCycleAsync(IReadOnlyList<ScannerResult> results, CancellationToken cancellationToken = default);

    /// <summary>All rows from the single most recent ScanTimestamp.</summary>
    Task<IReadOnlyList<ScannerResult>> GetLatestCycleAsync(CancellationToken cancellationToken = default);
}

/// <summary>BacktestRuns + the BacktestTrades link table + the BacktestRunId-scoped snapshot/drawdown tables (spec §5.5, §7.6 reproducibility).</summary>
public interface IBacktestRunRepository
{
    /// <summary>
    /// Inserts the run row at START (spec §7.6: "every backtest run persists
    /// a full config snapshot at start"), before the engine has actually
    /// produced results - Status is expected to be Running and the
    /// result-metric fields null at this point. Returns the new Id.
    /// </summary>
    Task<long> InsertStartedAsync(BacktestRun run, CancellationToken cancellationToken = default);

    /// <summary>Updates the same row with final metrics/Status/CompletedAt once BacktestEngine.Run has returned.</summary>
    Task UpdateCompletedAsync(BacktestRun run, CancellationToken cancellationToken = default);

    /// <summary>Marks a run Failed (engine threw) so it never sits at Status=Running forever after a crash.</summary>
    Task MarkFailedAsync(long id, DateTimeOffset failedAt, CancellationToken cancellationToken = default);

    Task LinkTradesAsync(long backtestRunId, IReadOnlyList<long> tradeIds, CancellationToken cancellationToken = default);
    Task InsertSnapshotsAsync(long backtestRunId, IReadOnlyList<PortfolioSnapshot> snapshots, CancellationToken cancellationToken = default);
    Task InsertDrawdownEventsAsync(long backtestRunId, IReadOnlyList<DrawdownEvent> drawdownEvents, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BacktestRun>> GetRunsAsync(CancellationToken cancellationToken = default);
    Task<BacktestRun?> GetRunAsync(long id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Trade>> GetTradesAsync(long backtestRunId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PortfolioSnapshot>> GetSnapshotsAsync(long backtestRunId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DrawdownEvent>> GetDrawdownEventsAsync(long backtestRunId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generic key-value persistence for the pure engine state records that
/// must survive between trading-cycle invocations — DrawdownState,
/// LossLimitState, KillSwitchState (Tranche 20). See SchemaDefinition's
/// remarks on the EngineState table for why this is JSON-blob-per-key
/// rather than three dedicated tables.
/// </summary>
public interface IEngineStateRepository
{
    /// <summary>Null when nothing has been stored under this key yet (e.g. the very first cycle ever run).</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
}

/// <summary>Daily market-regime snapshots (spec §3.1/§7.2 MarketRegimes table) — one row per as-of date, upserted so a re-run for the same day updates in place.</summary>
public interface IMarketRegimeRepository
{
    Task UpsertAsync(MarketRegimeSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<MarketRegimeSnapshot?> GetLatestAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MarketRegimeSnapshot>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>Parameter-optimization candidates (spec §5.5/§7.2 OptimizationRuns table). One <c>optimize</c> invocation writes one batch.</summary>
public interface IOptimizationRunRepository
{
    /// <summary>Inserts every candidate of one optimize invocation in a single transaction and returns the new BatchId.</summary>
    Task<string> InsertBatchAsync(IReadOnlyList<OptimizationRunRecord> candidates, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OptimizationRunRecord>> GetBatchAsync(string batchId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OptimizationRunRecord>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>Persists the score breakdown, VCP pattern and indicator snapshot behind each ranked scan row. Upserts by (Symbol, AsOfDate) / (Symbol, Timestamp, Timeframe), so rescans of the same session update in place.</summary>
public interface IScanEvidenceRepository
{
    Task UpsertAsync(IReadOnlyList<ScanEvidence> evidence, CancellationToken cancellationToken = default);
    Task<StockScore?> GetLatestScoreAsync(string symbol, CancellationToken cancellationToken = default);
    Task<VcpPattern?> GetLatestVcpAsync(string symbol, CancellationToken cancellationToken = default);
    Task<IndicatorSnapshot?> GetLatestIndicatorsAsync(string symbol, CancellationToken cancellationToken = default);
}

/// <summary>Imported NSE delivery statistics (spec §3.4 delivery %). One row per symbol and session; re-importing the same file updates in place.</summary>
public interface IDeliveryDataRepository
{
    Task UpsertRangeAsync(IReadOnlyList<DeliveryRecord> records, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryRecord>> GetRangeAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
    Task<DateOnly?> GetLatestDateAsync(CancellationToken cancellationToken = default);
}
