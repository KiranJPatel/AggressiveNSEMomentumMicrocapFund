namespace TradingPlatform.Data.Yahoo;

/// <summary>
/// Pure function: given a requested [from, to] date range and the set of
/// dates already cached, returns the minimal set of contiguous sub-ranges
/// that still need to be fetched (spec §5.2: "check cache → determine
/// missing ranges → download only missing ranges"). Weekends are never
/// counted as missing. No NSE holiday calendar is available in this build
/// environment (no network — see docs/gap-log.md), so holidays are NOT
/// excluded here; a holiday will show up as a "missing" weekday, get
/// requested from Yahoo, and simply come back with no data for that date —
/// harmless, just a wasted request. A real holiday calendar (ingested via
/// CSV, same pattern as ISurveillanceDataProvider) would tighten this later.
/// </summary>
public static class MissingRangeCalculator
{
    public static IReadOnlyList<(DateOnly From, DateOnly To)> Calculate(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> cachedDates)
    {
        if (to < from) throw new ArgumentException($"'to' ({to}) must not be before 'from' ({from}).");

        var ranges = new List<(DateOnly From, DateOnly To)>();
        DateOnly? runStart = null;
        var previous = from;

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var isMissing = !isWeekend && !cachedDates.Contains(date);

            if (isMissing)
            {
                runStart ??= date;
                previous = date;
            }
            else if (runStart is not null)
            {
                ranges.Add((runStart.Value, previous));
                runStart = null;
            }
        }

        if (runStart is not null)
            ranges.Add((runStart.Value, previous));

        return ranges;
    }
}
