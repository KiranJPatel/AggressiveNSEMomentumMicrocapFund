namespace TradingPlatform.Core.Enums;

/// <summary>
/// Surveillance / data-quality risk flags (spec §2.3). Combinable bitwise.
/// A flag being absent means "not flagged", not "confirmed safe" — the
/// distinction between Unknown and confirmed-clear lives in
/// <see cref="Models.SurveillanceStatus"/>, not in this enum alone.
/// </summary>
[Flags]
public enum RiskFlags
{
    None = 0,
    Asm = 1 << 0,
    Gsm = 1 << 1,
    TradeToTrade = 1 << 2,
    SurveillanceFlag = 1 << 3,
    CorporateActionRisk = 1 << 4,
    LiquidityRisk = 1 << 5,
    DataQualityRisk = 1 << 6
}
