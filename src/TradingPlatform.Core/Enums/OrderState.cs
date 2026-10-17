namespace TradingPlatform.Core.Enums;

/// <summary>
/// Order state machine (spec §6.3). Never assume a fill just because
/// submission succeeded — a Submitted order must transition explicitly.
/// </summary>
public enum OrderState
{
    Created = 0,
    Validated = 1,
    Submitted = 2,
    Acknowledged = 3,
    Open = 4,
    PartiallyFilled = 5,
    Filled = 6,
    Cancelled = 7,
    Rejected = 8,
    Failed = 9,
    Unknown = 10
}
