namespace TradingPlatform.Core.Enums;

/// <summary>Explicit trading mode (spec §6.1). Default is Paper.</summary>
public enum TradingMode
{
    DryRun = 0,
    Paper = 1,
    Live = 2
}
