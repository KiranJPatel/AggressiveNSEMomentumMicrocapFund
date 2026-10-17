namespace TradingPlatform.Core.Configuration;

/// <summary>Binds to config section "MarketRegime" (spec §3.1). No magic numbers in code.</summary>
public sealed class MarketRegimeOptions
{
    public const string SectionName = "MarketRegime";

    public int AggressiveThreshold { get; set; } = 80;
    public int NormalThreshold { get; set; } = 65;
    public int CautiousThreshold { get; set; } = 50;
    public int DefensiveThreshold { get; set; } = 35;

    public decimal AggressiveExposurePercent { get; set; } = 100m;
    public decimal NormalExposurePercent { get; set; } = 80m;
    public decimal CautiousExposurePercent { get; set; } = 50m;
    public decimal DefensiveExposurePercent { get; set; } = 25m;
    public decimal EmergencyExposurePercent { get; set; } = 10m;

    // --- Engine calculation parameters (MarketRegimeEngine, Tranche 5) ---

    /// <summary>SMA periods used for the index trend sub-score (Close vs SMA50/SMA200, golden-cross state, rising-200DMA).</summary>
    public int TrendSma50Period { get; set; } = 50;
    public int TrendSma200Period { get; set; } = 200;

    /// <summary>How many bars back to compare SMA200 against, to judge whether the long-term trend is rising.</summary>
    public int TrendSlopeLookbackDays { get; set; } = 20;

    /// <summary>Index historical-volatility period/annualization, reused from Indicators (Tranche 3).</summary>
    public int VolatilityPeriod { get; set; } = 20;
    public int VolatilityTradingDaysPerYear { get; set; } = 252;

    /// <summary>Annualized HV at/below this maps to a VolatilityScore of 100 ("calm").</summary>
    public decimal VolatilityLowThresholdPercent { get; set; } = 12m;

    /// <summary>Annualized HV at/above this maps to a VolatilityScore of 0 ("turbulent").</summary>
    public decimal VolatilityHighThresholdPercent { get; set; } = 35m;

    // RegimeScore composite weights (must sum to 100). Any component whose
    // input wasn't supplied (breadth/advance-decline/breakout-success
    // require universe-wide or trade-history data not yet available this
    // early in the roadmap — see MarketRegimeEngine class remarks) is
    // excluded and the remaining weights are renormalized, rather than
    // silently substituting a neutral value for missing data.
    public decimal NiftyTrendWeight { get; set; } = 30m;
    public decimal BreadthWeight { get; set; } = 20m;
    public decimal AdvanceDeclineWeight { get; set; } = 15m;
    public decimal SmallMidRelativeStrengthWeight { get; set; } = 15m;
    public decimal VolatilityWeight { get; set; } = 15m;
    public decimal BreakoutSuccessRateWeight { get; set; } = 5m;

    // --- Universe-derived inputs (UniverseBreadthCalculator / BreakoutSuccessRateCalculator, spec §3.1) ---

    /// <summary>Breadth and advance/decline are only reported when at least this many symbols have a usable bar that day; a handful of stocks says nothing about the market (null = unavailable, its weight is renormalized away).</summary>
    public int BreadthMinimumSampleSize { get; set; } = 20;

    /// <summary>Trades CLOSED within this many calendar days feed the breakout success rate.</summary>
    public int BreakoutSuccessLookbackDays { get; set; } = 180;

    /// <summary>Fewer closed trades than this in the window and the success rate is unavailable rather than computed from noise.</summary>
    public int BreakoutSuccessMinimumTrades { get; set; } = 10;

    /// <summary>
    /// When the broad index has too little history for the trend component (needs SMA200) the regime cannot be judged. True (default,
    /// spec §0.4 — never assume safe) forces RegimeScore 0 / zero deployable capital instead of letting volatility alone read a calm
    /// market as Aggressive. Set false only to deliberately trade a short-history index.
    /// </summary>
    public bool FailClosedWithoutTrend { get; set; } = true;

    // --- Weekly market bias (spec §7.3: weekly = market bias) ---

    /// <summary>Fast SMA period in WEEKS for the weekly bias (10 weeks is roughly the 50-day average).</summary>
    public int WeeklyFastPeriod { get; set; } = 10;

    /// <summary>Slow SMA period in WEEKS (30 weeks is roughly the 150-day average).</summary>
    public int WeeklySlowPeriod { get; set; } = 30;

    /// <summary>The fast weekly SMA must be higher than this many weeks ago for the slope signal.</summary>
    public int WeeklySlopeLookbackWeeks { get; set; } = 4;

    /// <summary>
    /// When set and the weekly bias is Bearish, deployable capital is capped at this percent (never raised). Null (default) = the weekly bias is
    /// computed, stored and shown but does not change exposure: acting on it is a strategy change that has not been validated on real data.
    /// </summary>
    public decimal? WeeklyBearishDeployableCapPercent { get; set; }
}
