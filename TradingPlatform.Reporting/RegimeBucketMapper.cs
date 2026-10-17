using TradingPlatform.Core.Enums;

namespace TradingPlatform.Reporting;

/// <summary>
/// Spec §8.1's report wants a 3-way "Regime — Bull: XX% Normal: XX%
/// Bear: XX%" breakdown, but MarketRegimeEngine (Tranche 5) classifies
/// into 5 tiers (Aggressive/Normal/Cautious/Defensive/Emergency) for
/// SIZING purposes, and spec never defines how the 5 collapse into 3 for
/// display. Resolved as: Aggressive+Normal → Bull (both deploy freely),
/// Cautious → Normal (the literal middle tier), Defensive+Emergency →
/// Bear (both are risk-off, capital-preserving stances) — a
/// display-only simplification with zero effect on any trading decision,
/// which continues to use the full 5-tier classification everywhere else.
/// </summary>
public static class RegimeBucketMapper
{
    public static string ToReportBucket(RegimeClassification classification) => classification switch
    {
        RegimeClassification.Aggressive or RegimeClassification.Normal => "Bull",
        RegimeClassification.Cautious => "Normal",
        RegimeClassification.Defensive or RegimeClassification.Emergency => "Bear",
        _ => "Unknown",
    };
}
