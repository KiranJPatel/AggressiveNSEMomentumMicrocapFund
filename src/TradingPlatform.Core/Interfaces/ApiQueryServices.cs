using TradingPlatform.Core.Models;

namespace TradingPlatform.Core.Interfaces;

/// <summary>
/// The eight query-service interfaces backing spec Section 7.5's API
/// surface (GET /api/portfolio /positions /orders /signals /scanner
/// /risk /performance /backtests). Each is a thin READ contract - the
/// Api project's endpoint handlers depend on these, not on any concrete
/// data source; the Application-layer host process is responsible for
/// implementing them (querying SQLite repositories and/or live state),
/// consistent with every prior tranche's "engine defines the seam,
/// Application layer wires the live implementation" split (Tranches 11,
/// 14, 15). Kept in one file since each is a single, tiny method group -
/// splitting eight one-method interfaces across eight files would add
/// navigation overhead for no benefit.
/// </summary>
public interface IPortfolioQueryService
{
    Task<PortfolioSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default);
}

/// <summary>Local/tracked open positions for API display - distinct from IPositionService (Tranche 12), which is specifically the BROKER-facing reconciliation contract.</summary>
public interface IPositionQueryService
{
    Task<IReadOnlyList<Position>> GetOpenPositionsAsync(CancellationToken cancellationToken = default);
}

public interface IOrderQueryService
{
    Task<IReadOnlyList<Order>> GetRecentOrdersAsync(int limit, CancellationToken cancellationToken = default);
}

public interface ISignalQueryService
{
    Task<IReadOnlyList<Signal>> GetRecentSignalsAsync(int limit, CancellationToken cancellationToken = default);
}

public interface IScannerQueryService
{
    Task<IReadOnlyList<ScannerResult>> GetLatestResultsAsync(CancellationToken cancellationToken = default);
}

public interface IRiskQueryService
{
    Task<RiskStatusDto> GetCurrentStatusAsync(CancellationToken cancellationToken = default);
}

public interface IPerformanceQueryService
{
    Task<PerformanceSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);
}

public interface IBacktestQueryService
{
    Task<IReadOnlyList<BacktestRun>> GetRunsAsync(CancellationToken cancellationToken = default);
    Task<BacktestRun?> GetRunAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>The rich per-run view (trades/equity-curve/drawdown-events/yearly-returns) backing GET /api/backtests/{id}/detail — see BacktestRunDetailDto's remarks.</summary>
    Task<BacktestRunDetailDto?> GetRunDetailAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>Backs GET /api/metrics (Tranche 21) — see SystemMetricsDto's remarks.</summary>
public interface IMetricsQueryService
{
    Task<SystemMetricsDto> GetCurrentMetricsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Backs GET /api/chart/{symbol} — see ChartDataDto's remarks.</summary>
public interface IChartQueryService
{
    Task<ChartDataDto?> GetChartDataAsync(string symbol, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>Backs GET /api/instruments — the active universe, for a dashboard's symbol picker.</summary>
public interface IInstrumentQueryService
{
    Task<IReadOnlyList<Instrument>> GetActiveInstrumentsAsync(CancellationToken cancellationToken = default);
}
