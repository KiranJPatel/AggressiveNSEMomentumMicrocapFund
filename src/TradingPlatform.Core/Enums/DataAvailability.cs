namespace TradingPlatform.Core.Enums;

/// <summary>
/// Explicit tri-state for any field that can be legitimately unavailable
/// (fundamentals, surveillance status, sector, market-cap bucket, etc.).
/// Per spec §0.4 / §2.3 / §3.2: missing data must never silently default
/// to "safe" or "positive" — callers must branch on this before using the
/// associated value.
/// </summary>
public enum DataAvailability
{
    Unknown = 0,
    Available = 1,
    Unavailable = 2
}
