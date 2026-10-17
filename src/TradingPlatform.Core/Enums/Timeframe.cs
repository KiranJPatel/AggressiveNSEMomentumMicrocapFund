namespace TradingPlatform.Core.Enums;

/// <summary>Multi-timeframe support (spec §7.3). Daily is primary/required for v1.</summary>
public enum Timeframe
{
    Weekly = 0,
    Daily = 1,
    H1 = 2,
    M15 = 3,
    M5 = 4
}
