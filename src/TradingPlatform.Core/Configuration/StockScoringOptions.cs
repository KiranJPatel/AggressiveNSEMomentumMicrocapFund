namespace TradingPlatform.Core.Configuration;

/// <summary>
/// Binds to config section "StockScoring" (spec §3.2). The 9 component
/// WEIGHTS (RelativeStrength=20, VolumeAccumulation=15, etc.) are already
/// pinned down precisely in <see cref="StrategyOptions.Weights"/> — this
/// class holds the parameters each component CALCULATOR needs to turn raw
/// data into a 0-100 sub-score, which the spec doesn't specify numerically
/// (another set of resolved ambiguities, per §0.4, documented here rather
/// than hardcoded).
/// </summary>
public sealed class StockScoringOptions
{
    public const string SectionName = "StockScoring";

    /// <summary>How many recent bars' RelativeVolume to average for the "Volume Accumulation" component (spec §3.2) — sustained above-average volume, not a single day's spike.</summary>
    public int VolumeAccumulationWindowBars { get; set; } = 10;

    /// <summary>How far above/below RelativeVolume=1.0 (average) maps to a saturated 0/100 accumulation score.</summary>
    public decimal VolumeAccumulationNormalizationScale { get; set; } = 0.5m;

    /// <summary>Lookback period for the raw "Price Momentum" component (absolute return, not vs. a benchmark — see RelativeStrength for that).</summary>
    public int PriceMomentumPeriodBars { get; set; } = 60;

    /// <summary>% price move over PriceMomentumPeriodBars that maps to a saturated 0/100 momentum score.</summary>
    public decimal PriceMomentumNormalizationScalePercent { get; set; } = 40m;

    /// <summary>Percentage-point acceleration (current growth % - prior growth %) that maps to a saturated 0/100 earnings/revenue acceleration score.</summary>
    public decimal FundamentalAccelerationNormalizationScalePoints { get; set; } = 15m;

    /// <summary>How many bars back the "prior period" fundamental snapshot should be, for measuring acceleration rather than a single-point level.</summary>
    public int FundamentalPriorPeriodMonths { get; set; } = 3;

    // --- Delivery % (spec §3.4 / §3.2 volume component). Off by default: it needs imported NSE delivery data (`import-delivery`). ---

    /// <summary>Blend delivery % into the Volume Accumulation component. With no delivery data for a symbol/day the volume score is left exactly as it was (no bonus, no penalty).</summary>
    public bool UseDeliveryPercent { get; set; } = false;

    /// <summary>Share (percent) of the Volume Accumulation score that comes from delivery when it is available; the rest stays the relative-volume score.</summary>
    public decimal DeliveryBlendWeightPercent { get; set; } = 30m;

    /// <summary>Today's delivery % divided by the stock's own recent average: at or below this ratio the delivery score is 0.</summary>
    public decimal DeliveryRatioForZeroScore { get; set; } = 0.8m;

    /// <summary>At or above this ratio the delivery score is 100 (linear in between).</summary>
    public decimal DeliveryRatioForFullScore { get; set; } = 1.5m;

    /// <summary>How many previous sessions' delivery records form the baseline average.</summary>
    public int DeliveryLookbackDays { get; set; } = 20;

    /// <summary>Fewer baseline sessions than this and no delivery snapshot is produced at all.</summary>
    public int DeliveryMinSampleDays { get; set; } = 10;
}
