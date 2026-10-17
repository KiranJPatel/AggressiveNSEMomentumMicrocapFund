namespace TradingPlatform.Core.Models;

public enum PriceStructureState
{
    /// <summary>Fewer than two confirmed swing highs or two confirmed swing lows — structure cannot be read yet.</summary>
    Insufficient,
    /// <summary>Latest swing high is a higher high AND latest swing low is a higher low.</summary>
    Uptrend,
    /// <summary>Latest swing high is a lower high AND latest swing low is a lower low.</summary>
    Downtrend,
    /// <summary>Highs and lows disagree (e.g. higher high but lower low): a range or a transition.</summary>
    Mixed,
}

/// <summary>Spec §3.4 "price structure": HH/HL/LH/LL reading plus the nearest confirmed support below and resistance above the close. Support/Resistance are null when no confirmed swing sits on that side (e.g. price at new highs has no overhead resistance).</summary>
public sealed record PriceStructure(PriceStructureState State, decimal? Support, decimal? Resistance, string? LastHighLabel, string? LastLowLabel);
