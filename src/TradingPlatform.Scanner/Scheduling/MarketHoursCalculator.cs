using TradingPlatform.Core.Configuration;

namespace TradingPlatform.Scanner.Scheduling;

/// <summary>
/// Spec §7.1's scheduled scanner needs to know when to actually run —
/// pure, testable market-hours logic kept separate from
/// ScannerBackgroundService's actual timer/hosting mechanics.
///
/// Documented gap: this only knows weekday-vs-weekend, not the NSE
/// trading calendar's specific holidays — spec doesn't supply a holiday
/// list, and hard-coding one would go stale every year regardless. A
/// scan attempted on an actual NSE holiday will find no fresh bar data
/// for "today" from whatever data source feeds it and naturally produce
/// nothing new to act on, so this gap costs an occasional wasted scan
/// cycle, not a correctness failure — a deliberate, honest simplification
/// rather than a hidden one.
/// </summary>
public static class MarketHoursCalculator
{
    public static bool IsMarketOpen(DateTimeOffset nowUtc, ScannerOptions options)
    {
        var localNow = ToLocal(nowUtc, options);
        if (localNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;

        var open = TimeOnly.Parse(options.MarketOpenLocalTime);
        var close = TimeOnly.Parse(options.MarketCloseLocalTime);
        var timeOfDay = TimeOnly.FromDateTime(localNow.DateTime);
        return timeOfDay >= open && timeOfDay <= close;
    }

    /// <summary>How long until the market is next open, from nowUtc — zero if already open.</summary>
    public static TimeSpan TimeUntilNextOpen(DateTimeOffset nowUtc, ScannerOptions options)
    {
        if (IsMarketOpen(nowUtc, options)) return TimeSpan.Zero;

        var tz = TimeZoneInfo.FindSystemTimeZoneById(options.MarketTimeZoneId);
        var localNow = ToLocal(nowUtc, options);
        var open = TimeOnly.Parse(options.MarketOpenLocalTime);

        var candidateLocalDate = localNow.Date;
        if (localNow.TimeOfDay > TimeOnly.Parse(options.MarketCloseLocalTime).ToTimeSpan())
            candidateLocalDate = candidateLocalDate.AddDays(1); // already past today's close — next open is a later day

        while (true)
        {
            var dayOfWeek = candidateLocalDate.DayOfWeek;
            if (dayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                var candidateLocalOpen = candidateLocalDate.Add(open.ToTimeSpan());
                if (candidateLocalOpen > localNow.DateTime)
                {
                    var candidateOffset = new DateTimeOffset(DateTime.SpecifyKind(candidateLocalOpen, DateTimeKind.Unspecified), tz.GetUtcOffset(candidateLocalOpen));
                    return candidateOffset.ToUniversalTime() - nowUtc;
                }
            }
            candidateLocalDate = candidateLocalDate.AddDays(1);
        }
    }

    private static DateTimeOffset ToLocal(DateTimeOffset nowUtc, ScannerOptions options)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(options.MarketTimeZoneId);
        return TimeZoneInfo.ConvertTime(nowUtc, tz);
    }
}
