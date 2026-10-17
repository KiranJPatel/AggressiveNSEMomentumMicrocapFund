using TradingPlatform.Core.Configuration;

namespace TradingPlatform.Scanner.Scheduling;

public enum DailyJobKind { PreMarket, PostMarket }

/// <summary>
/// When the spec §7.1 pre-market / post-market jobs next run. Pure and testable (no clock, no hosting). Weekday-only,
/// like MarketHoursCalculator — NSE holidays are not modelled (a holiday run just finds nothing new and is harmless).
/// </summary>
public static class DailyJobSchedule
{
    public static DateTimeOffset NextRun(DateTimeOffset nowUtc, ScannerOptions options, DailyJobKind kind)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(options.MarketTimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime;
        var offset = kind == DailyJobKind.PreMarket
            ? TimeOnly.Parse(options.MarketOpenLocalTime).ToTimeSpan() - TimeSpan.FromMinutes(options.PreMarketLeadMinutes)
            : TimeOnly.Parse(options.MarketCloseLocalTime).ToTimeSpan() + TimeSpan.FromMinutes(options.PostMarketDelayMinutes);

        var day = localNow.Date;
        for (var i = 0; i < 10; i++, day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var candidate = day.Add(offset);
            if (candidate <= localNow) continue;
            var unspecified = DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, tz.GetUtcOffset(unspecified)).ToUniversalTime();
        }
        throw new InvalidOperationException("Could not find a next weekday run within 10 days — check the market-hours configuration.");
    }
}
