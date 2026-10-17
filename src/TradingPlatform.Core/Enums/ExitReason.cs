namespace TradingPlatform.Core.Enums;

/// <summary>
/// The four independent exit mechanisms from spec §4.3, plus the
/// bookkeeping reasons needed to close out a position/trade record.
/// </summary>
public enum ExitReason
{
    StopLoss = 0,              // (A) immediate
    MomentumFailure = 1,       // (B) RS deterioration + HMA breakdown + volume distribution
    FundamentalFailure = 2,    // (C) severe fundamental deterioration (where data available)
    RegimeFailure = 3,         // (D) portfolio-level exposure reduction
    TrailingStopHit = 4,
    ManualClose = 5,
    KillSwitch = 6
}
