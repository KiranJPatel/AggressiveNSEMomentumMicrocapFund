namespace TradingPlatform.Core.Configuration;

using TradingPlatform.Core.Enums;

/// <summary>Binds to config section "Strategy" (spec §3.1-§3.8). No magic numbers in code.</summary>
public sealed class StrategyOptions
{
    public const string SectionName = "Strategy";

    public string StrategyVersion { get; set; } = "AggressiveMomentumV1";
    public string ConfigurationVersion { get; set; } = "v1";

    // Staged entry (spec §3.7)
    public decimal Stage1PercentOfPosition { get; set; } = 40m;
    public decimal Stage2PercentOfPosition { get; set; } = 30m;
    public decimal Stage3PercentOfPosition { get; set; } = 30m;

    // Stage 2 trigger: "+2-3% favorable move, trend still valid, no stop
    // violation" (spec §3.7). The 2-3% window's operative trigger point is
    // the MIN threshold — see Strategy.Entry.Stage2TriggerEvaluator's XML
    // doc for why Max is documentation of the typical range rather than an
    // upper ceiling that would perversely block adding on a stronger move.
    public decimal Stage2TriggerFavorableMoveMinPercent { get; set; } = 2m;
    public decimal Stage2TriggerFavorableMoveMaxPercent { get; set; } = 3m;

    // Stage 3 trigger: "successful breakout retest, support holds, momentum
    // constructive" (spec §3.7) — a qualitatively different, retest-based
    // pattern from Stage 2's simple percentage move, not "another 2-3% up"
    // (see Strategy.Entry.Stage3TriggerEvaluator). A pullback of at least
    // this % since the Stage 2 fill is what makes it a genuine "retest"
    // rather than uninterrupted continuation.
    public decimal Stage3MinRetestPullbackPercent { get; set; } = 3m;

    // Breakout engine (spec §3.6)
    public decimal BreakoutVolumeMultiplier { get; set; } = 1.5m;
    public decimal BreakoutMinAdx { get; set; } = 20m;

    // Trailing ladder (spec §4.3)
    public decimal TrailingBreakevenTriggerR { get; set; } = 1m;
    public decimal TrailingActiveTriggerR { get; set; } = 2m;
    public decimal TrailingAggressiveTriggerR { get; set; } = 3m;

    // Execution timing (spec §3.8)
    public SignalToOrderLag SignalToOrderLag { get; set; } = SignalToOrderLag.NextOpen;

    // Relative strength periods (spec §3.3)
    public int[] RelativeStrengthPeriods { get; set; } = { 20, 50, 90, 180 };

    // Stock scoring weights (spec §3.2) - must sum to 100
    public ScoringWeights Weights { get; set; } = new();

    /// <summary>
    /// Optional breakout gate (spec §3.4 delivery %): require today's delivery % to be at least this multiple of the stock's recent average.
    /// Null (default) = no delivery condition. When SET, a symbol whose delivery data is missing for the signal day FAILS the gate (cannot confirm
    /// — fails closed), so only enable it once delivery data is being imported for the whole universe.
    /// </summary>
    public decimal? BreakoutMinDeliveryRatio { get; set; }
}

public sealed class ScoringWeights
{
    public decimal RelativeStrength { get; set; } = 20m;
    public decimal VolumeAccumulation { get; set; } = 15m;
    public decimal PriceMomentum { get; set; } = 15m;
    public decimal EarningsAcceleration { get; set; } = 15m;
    public decimal VcpQuality { get; set; } = 10m;
    public decimal RevenueAcceleration { get; set; } = 10m;
    public decimal TrendStructure { get; set; } = 5m;
    public decimal InstitutionalActivity { get; set; } = 5m;
    public decimal SectorStrength { get; set; } = 5m;

}
