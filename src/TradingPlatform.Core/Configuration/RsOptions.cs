namespace TradingPlatform.Core.Configuration;

/// <summary>
/// Binds to config section "RelativeStrength" (spec §3.3). No magic numbers
/// in code.
///
/// Methodology: per-period relative outperformance is the period-over-period
/// % change in the Mansfield "Relative Performance" ratio (RP = StockClose /
/// BenchmarkClose — confirmed via web search against Stan Weinstein's
/// original formula and multiple corroborating sources), rather than
/// Mansfield's own single-period 52-week-SMA oscillator design — the spec
/// asks for four distinct periods (20/50/90/180), which a single smoothed
/// oscillator doesn't naturally provide. Each period's raw % outperformance
/// is then squashed to 0-100 via a symmetric tanh curve (50 = exactly
/// matching the benchmark), rather than hard-clamped linear scaling, so
/// extreme values saturate smoothly instead of pinning at the boundary.
/// Both the periods-to-weights mapping and the squash scale are resolved
/// ambiguities (spec §0.4) — documented here, not silently guessed at.
/// </summary>
public sealed class RsOptions
{
    public const string SectionName = "RelativeStrength";

    public int[] Periods { get; set; } = { 20, 50, 90, 180 };

    /// <summary>Composite weight per period, same order as Periods (must sum to 100). Nearest-term weighted heaviest, consistent with common momentum-scoring practice.</summary>
    public decimal[] PeriodWeights { get; set; } = { 40m, 30m, 20m, 10m };

    /// <summary>% outperformance that maps to tanh(1) ≈ a score of ~88 — i.e. how "wide" the normalization curve is.</summary>
    public decimal NormalizationScalePercent { get; set; } = 20m;

    /// <summary>Blend weights across benchmarks when more than one is supplied (spec §3.3: "Nifty 500, sector benchmark, and midcap benchmark where applicable"), renormalized over whichever are actually available.</summary>
    public decimal BroadIndexWeight { get; set; } = 50m;
    public decimal SectorBenchmarkWeight { get; set; } = 25m;
    public decimal MidcapBenchmarkWeight { get; set; } = 25m;

    /// <summary>System-wide "substantially outperforming" bar (composite score) referenced by the Scoring/Breakout engines (spec §3.3, §3.6).</summary>
    public decimal SubstantialOutperformanceScoreThreshold { get; set; } = 70m;
}
