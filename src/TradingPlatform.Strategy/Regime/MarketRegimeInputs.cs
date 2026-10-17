using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Regime;

/// <summary>
/// Inputs to MarketRegimeEngine. NiftyTrendScore and VolatilityScore are
/// always computable from BroadIndexBars alone; SmallMidRelativeStrength
/// is computable when MidcapIndexBars is supplied. Breadth,
/// advance/decline, and breakout-success-rate genuinely require
/// universe-wide statistics or trade-history that don't exist this early
/// in the roadmap (Scanner is Tranche 14; live/backtest trade history is
/// later still) — they're accepted as pre-computed optional inputs so the
/// engine can be exercised meaningfully today, and their weights are
/// dropped and renormalized when absent rather than defaulted to a
/// fabricated neutral value (spec §0.4: missing data must never silently
/// default to a positive/neutral score).
/// </summary>
public sealed class MarketRegimeInputs
{
    public required IReadOnlyList<HistoricalBar> BroadIndexBars { get; init; }
    public IReadOnlyList<HistoricalBar>? MidcapIndexBars { get; init; }

    public decimal? PercentUniverseAbove50Dma { get; init; }
    public decimal? PercentUniverseAbove200Dma { get; init; }
    public decimal? AdvanceDeclineRatio { get; init; }
    public decimal? BreakoutSuccessRatePercent { get; init; }
}
