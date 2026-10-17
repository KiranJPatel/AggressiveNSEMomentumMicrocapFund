using TradingPlatform.Core.Enums;

namespace TradingPlatform.Risk.Sizing;

/// <summary>
/// Inputs to PositionSizingCalculator. Every "current exposure" field
/// (sector, microcap, portfolio heat, correlation cluster) is accepted
/// pre-aggregated rather than recomputed from a raw position list here —
/// aggregating the whole portfolio's exposure is a portfolio-level
/// concern the caller (Backtesting/Execution orchestration, later
/// tranches) owns; this calculator's job is purely the MIN-of-constraints
/// arithmetic for one candidate trade against those already-known totals.
/// </summary>
public sealed class PositionSizingInputs
{
    public required string Symbol { get; init; }
    public required decimal EntryPrice { get; init; }
    public required decimal StopPrice { get; init; }

    public required decimal Nav { get; init; }
    public required decimal CashBalance { get; init; }
    public required decimal AverageDailyTradedValue { get; init; }

    public required decimal StockScoreTotalScore { get; init; }
    public required MarketCapBucket MarketCapBucket { get; init; }

    public required decimal CurrentSectorExposureValue { get; init; }
    public required decimal CurrentMicrocapExposureValue { get; init; }
    public required decimal CurrentPortfolioHeatValue { get; init; }
    public required decimal CorrelatedClusterExposureValue { get; init; }
    public required decimal DeployableCapitalPercent { get; init; }

    // Microcap-only additional gates (spec §4.2) — null/false is a
    // legitimate "not applicable" for a non-microcap candidate, but for a
    // MICROCAP candidate a null score is treated as failing the gate (see
    // PositionSizingCalculator), never silently passed.
    public decimal? TechnicalScore { get; init; }
    public decimal? FundamentalScore { get; init; }
    public bool HasCriticalSurveillanceFlag { get; init; }
    public bool HasSevereDataQualityIssue { get; init; }
}
