namespace TradingPlatform.Core.Enums;

/// <summary>
/// AMFI/SEBI semi-annual market-cap-rank bucket (see spec §2.2).
/// Rank 1-100 = Large, 101-250 = Mid, 251+ = Small; Micro is a configurable
/// absolute-market-cap sub-threshold within Small. Never inferred from
/// current-day market cap for historical dates — use point-in-time
/// <see cref="Models.UniverseClassificationSnapshot"/> records instead.
/// </summary>
public enum MarketCapBucket
{
    Unknown = 0,
    Large = 1,
    Mid = 2,
    Small = 3,
    Micro = 4
}
