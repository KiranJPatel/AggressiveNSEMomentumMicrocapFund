using TradingPlatform.Core.Enums;

namespace TradingPlatform.Core.Models;

/// <summary>
/// Stable API response shape for GET /api/risk (spec Section 7.5) -
/// deliberately its own flat DTO rather than exposing DrawdownState/
/// KillSwitchState (Tranches 10/12) directly, so the public API contract
/// stays stable even if those internal engine state shapes change.
/// </summary>
public sealed record RiskStatusDto(
    decimal CurrentDrawdownPercent,
    decimal MaxDrawdownPercent,
    string CurrentTierName,
    bool TradingShutDown,
    decimal PortfolioHeatPercent,
    decimal PortfolioHeatMaxPercent,
    bool DailyLossLimitBreached,
    bool WeeklyLossLimitBreached,
    bool MonthlyLossLimitBreached,
    bool KillSwitchActive,
    string? KillSwitchSource,
    string? KillSwitchReason);

/// <summary>Stable API response shape for GET /api/performance - a live/paper trading-to-date analogue of Tranche 16's BacktestReport, minus the backtest-only fields (WorstMonth, Regime distribution, SurvivorshipBiased) that don't apply outside a backtest.</summary>
public sealed record PerformanceSummaryDto(
    decimal CurrentCapital,
    decimal? CagrToDate,
    decimal? MaxDrawdownPercent,
    decimal? Sharpe,
    decimal? Sortino,
    decimal? WinRatePercent,
    decimal? ProfitFactor,
    decimal? ExpectancyR,
    int TradeCount);

/// <summary>
/// Stable API response shape for GET /api/metrics (Tranche 21, spec
/// §7.5's observability requirement: "current portfolio heat/drawdown/
/// regime" plus signal/order counts). Current-state fields come straight
/// from the latest PortfolioSnapshot; the four counts are computed over
/// the rows with Timestamp/GeneratedAt/CreatedAt on AsOf's calendar date
/// - a lightweight "today's activity" view, not a full time-series
/// metrics backend (no Prometheus/StatsD-equivalent is available in this
/// sandbox - see docs/gap-log.md). Latency metrics (data-fetch, scanner,
/// backtest duration) are emitted as structured log lines at each
/// command's completion instead of exposed here, since they're
/// per-invocation events rather than continuously-queryable state.
/// </summary>
public sealed record SystemMetricsDto(
    decimal? CurrentPortfolioHeatPercent,
    decimal? CurrentDrawdownPercent,
    RegimeClassification? CurrentRegime,
    int OpenPositionCount,
    int SignalsGeneratedToday,
    int OrdersSubmittedToday,
    int OrdersFilledToday,
    int OrdersRejectedToday,
    DateTimeOffset AsOf);

/// <summary>
/// Stable API response shape for GET /api/chart/{symbol} (dashboard
/// support, added alongside the HTML dashboard). Bars and Indicators are
/// parallel lists (same length, same order, index-for-index — exactly
/// how IndicatorEngine.ComputeSeries produces them from Bars in the
/// first place) covering the requested [from, to] window; any earlier
/// bars used purely as indicator warm-up are not included in either
/// list. Vcp/RelativeStrengthScore/MarketCapBucket/Surveillance are
/// each independently nullable/Unknown/absent per this codebase's
/// established "never fabricate a positive when data is missing"
/// discipline, not guaranteed-present fields.
/// </summary>
public sealed record ChartDataDto(
    string Symbol,
    IReadOnlyList<HistoricalBar> Bars,
    IReadOnlyList<IndicatorSnapshot> Indicators,
    VcpPattern? Vcp,
    decimal? RelativeStrengthScore,
    MarketCapBucket MarketCapBucket,
    SurveillanceStatus? Surveillance);

/// <summary>One calendar year's slice of a backtest run's equity curve, for GET /api/backtests/{id}/detail's yearly-returns table.</summary>
public sealed record YearlyReturnDto(string PeriodLabel, decimal ReturnPct, int Trades);

/// <summary>
/// Stable API response shape for GET /api/backtests/{id}/detail — the
/// rich per-run view (trades, equity curve, drawdown events, yearly
/// breakdown) that GET /api/backtests/{id} deliberately does not return
/// (that route stays a flat BacktestRun row, matching every other list
/// endpoint's item shape in this API).
/// </summary>
public sealed record BacktestRunDetailDto(
    BacktestRun Run,
    IReadOnlyList<Trade> Trades,
    IReadOnlyList<PortfolioSnapshot> Snapshots,
    IReadOnlyList<DrawdownEvent> DrawdownEvents,
    IReadOnlyList<YearlyReturnDto> YearlyReturns);
