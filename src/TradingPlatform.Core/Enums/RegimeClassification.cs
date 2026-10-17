namespace TradingPlatform.Core.Enums;

/// <summary>Market regime classification (spec §3.1). Score 0-100 buckets.</summary>
public enum RegimeClassification
{
    Emergency = 0,   // <35   -> 0-10% exposure
    Defensive = 1,   // 35-49 -> 25% exposure
    Cautious = 2,    // 50-64 -> 50% exposure
    Normal = 3,      // 65-79 -> 80% exposure
    Aggressive = 4   // 80-100 -> 100% exposure
}
