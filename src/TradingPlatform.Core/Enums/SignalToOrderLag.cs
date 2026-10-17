namespace TradingPlatform.Core.Enums;

/// <summary>
/// Resolved execution-timing ambiguity (spec §3.8). Default is NextOpen:
/// signal evaluated on confirmed daily close, order eligible for the next
/// session's open. EntryPrice == Close is never assumed.
/// </summary>
public enum SignalToOrderLag
{
    NextOpen = 0,
    Refinement60m = 1,
    Refinement15m = 2
}
