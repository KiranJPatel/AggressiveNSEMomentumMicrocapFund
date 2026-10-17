namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>
/// Surveillance status for one symbol as of a date (spec §2.3). Absence of
/// a record, or an explicit Unknown, must never be treated as "Safe" by
/// any caller.
/// </summary>
public sealed class SurveillanceStatus
{
    public required string Symbol { get; set; }
    public DateOnly AsOfDate { get; set; }
    public RiskFlags Flags { get; set; }
    public DataAvailability Availability { get; set; }
    public string? Source { get; set; }
}
