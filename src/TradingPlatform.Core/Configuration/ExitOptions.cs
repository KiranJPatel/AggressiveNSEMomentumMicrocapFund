namespace TradingPlatform.Core.Configuration;

/// <summary>
/// Binds to config section "Exit" (spec §4.3). Covers both the four exit
/// mechanisms and the trailing-stop ladder. Every threshold here is a
/// resolved ambiguity (spec §0.4): the spec names the SHAPE of each
/// mechanism ("RS deterioration + HMA breakdown + volume distribution",
/// "severe deterioration", "+1R -> breakeven") without pinning down exact
/// numeric cutoffs — documented in each calculator's XML doc, not silently
/// hardcoded.
/// </summary>
public sealed class ExitOptions
{
    public const string SectionName = "Exit";

    // --- Mechanism B: Momentum failure ---
    public decimal MomentumFailureRsThreshold { get; set; } = 40m;
    public decimal DistributionVolumeMultiplier { get; set; } = 1.5m;

    // --- Mechanism C: Fundamental failure (only evaluated where data is available) ---
    public decimal SevereFundamentalDeclinePercent { get; set; } = -15m;

    // --- Trailing stop ladder (spec §4.3) ---
    public int TrailingAtrPeriod { get; set; } = 14;
    public decimal TrailingAtrMultiplier { get; set; } = 3.0m;
    public int TrailingSwingLookback { get; set; } = 3;
}
