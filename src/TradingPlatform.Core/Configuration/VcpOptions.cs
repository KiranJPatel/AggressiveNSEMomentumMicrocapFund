namespace TradingPlatform.Core.Configuration;

/// <summary>
/// Binds to config section "Vcp" (spec §3.5). Every threshold here is a
/// resolved ambiguity, not an arbitrary guess: the VCP literature (Mark
/// Minervini's "Trade Like a Stock Market Wizard" / "Think & Trade Like a
/// Champion", corroborated by multiple independent secondary sources
/// consulted via web search) consistently describes the pattern's SHAPE
/// (progressively smaller pullbacks, 2-6 of them, volume drying up, a
/// tight final leg) without pinning down exact numeric cutoffs — different
/// practitioners and platforms use different specific percentages. These
/// defaults are documented, sensible, and — per spec §0.4 — fully
/// configurable rather than hardcoded in VcpDetector itself.
///
/// Deliberate scope boundary: VcpDetector only judges the CONTRACTION SHAPE
/// from OHLCV bars. It does not check Minervini's broader "Trend Template"
/// (price above a rising 200-day SMA, Stage 2 market structure, relative
/// strength vs. the index) — that is a separate regime/trend concern
/// (spec §3.1, §3.3) applied by the Stock Scoring engine (Tranche 6), which
/// combines VcpScore with TrendStructureScore and RelativeStrengthScore
/// rather than duplicating that logic inside the pattern detector.
/// </summary>
public sealed class VcpOptions
{
    public const string SectionName = "Vcp";

    /// <summary>Lookback/lookforward window for confirming a swing high/low (passed to SwingPointDetector).</summary>
    public int SwingLookback { get; set; } = 3;

    /// <summary>Minimum % price rise required before the base begins, to gate out bases forming in a downtrend rather than after an uptrend.</summary>
    public decimal MinPriorUptrendPercent { get; set; } = 20m;

    /// <summary>How far back (in bars) to look for the uptrend's starting low, relative to the base's first swing high.</summary>
    public int PriorUptrendLookbackBars { get; set; } = 60;

    public int MinContractions { get; set; } = 2;
    public int MaxContractions { get; set; } = 6;

    /// <summary>Each contraction's depth must be at most this fraction of the previous contraction's depth (0.80 = at least 20% tighter each time).</summary>
    public decimal MaxContractionRatioOfPrevious { get; set; } = 0.80m;

    /// <summary>A final contraction at or under this depth is scored as "tight" (spec §3.5 / literature: base should compress toward a tight pivot area).</summary>
    public decimal TightFinalContractionPercent { get; set; } = 12m;

    /// <summary>How much a contraction's low may undercut the previous contraction's low and still score as a "higher low" for scoring purposes (not a hard rejection — see VcpOptions class remarks).</summary>
    public decimal LowUndercutTolerancePercent { get; set; } = 2m;

    // VcpScore composite weights (must sum to 100) — spec §3.5.
    public decimal ContractionCountWeight { get; set; } = 20m;
    public decimal TighteningConsistencyWeight { get; set; } = 25m;
    public decimal VolumeDryUpWeight { get; set; } = 25m;
    public decimal HigherLowsWeight { get; set; } = 15m;
    public decimal FinalTightnessWeight { get; set; } = 15m;
}
