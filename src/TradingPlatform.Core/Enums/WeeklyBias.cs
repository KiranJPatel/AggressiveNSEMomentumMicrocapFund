namespace TradingPlatform.Core.Enums;

/// <summary>Weekly-timeframe market bias of the broad index (spec §7.3: "weekly = market bias").</summary>
public enum WeeklyBias
{
    /// <summary>Not enough weekly history to judge — never treated as Bullish.</summary>
    Unknown = 0,
    Bearish = 1,
    Neutral = 2,
    Bullish = 3,
}
