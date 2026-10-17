namespace TradingPlatform.Core.Abstractions;

/// <summary>
/// India Standard Time is a fixed UTC+05:30 offset with no daylight saving,
/// by law, always. This is a physical/calendar constant (like the length of
/// a day), not a business/strategy threshold — the spec's "no magic numbers"
/// principle (§0.4) targets tunable risk/strategy parameters, which this is
/// not. Used instead of a TimeZoneInfo lookup so bar-timestamp normalization
/// doesn't depend on the OS tzdata database being present/current.
/// </summary>
public static class IndiaStandardTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(5.5);
}
