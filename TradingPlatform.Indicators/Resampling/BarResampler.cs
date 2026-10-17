using System.Globalization;
using TradingPlatform.Core.Abstractions;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Indicators.Resampling;

/// <summary>
/// Builds coarser bars from finer cached ones (spec §7.3 multi-timeframe): daily -> weekly, and intraday -> intraday
/// (5m -> 15m / 60m, 15m -> 60m). Pure and point-in-time: a bar is built only from the bars given, so resampling a series that stops at
/// date D can never contain information from after D — the last (possibly still-forming) bucket simply reflects data up to D.
/// </summary>
public static class BarResampler
{
    private const string ResampledSource = "resampled";

    /// <summary>NSE regular session open (IST). Intraday buckets are anchored here so 60m bars start 09:15, 10:15, ... exactly like Yahoo's hourly candles.</summary>
    public static readonly TimeSpan SessionOpen = new(9, 15, 0);

    /// <summary>
    /// Weekly bars keyed by ISO week (Monday-first) of each bar's date. Open = first session's open, High/Low = extremes, Close/AdjustedClose =
    /// last session's, Volume = sum; Timestamp = the LAST session's timestamp in that week (the week "completes" on its last trading day).
    /// </summary>
    public static IReadOnlyList<HistoricalBar> ToWeekly(IReadOnlyList<HistoricalBar> daily)
    {
        var ordered = daily.OrderBy(b => b.Timestamp).ToList();
        var result = new List<HistoricalBar>();
        foreach (var week in ordered.GroupBy(b => (ISOWeek.GetYear(b.Timestamp.DateTime), ISOWeek.GetWeekOfYear(b.Timestamp.DateTime))))
            result.Add(Aggregate(week.ToList(), week.Last().Timestamp, Timeframe.Weekly));
        return result;
    }

    /// <summary>
    /// Intraday -> coarser intraday. Buckets are [09:15 + k*N min, 09:15 + (k+1)*N min) within each session date; the last bucket of the day
    /// may be shorter (e.g. the 15:15 hourly candle covers 15 minutes), as on Yahoo. Timestamp = bucket start.
    /// </summary>
    /// <exception cref="ArgumentException">Target is not a strict multiple of the source timeframe, or either is not intraday.</exception>
    public static IReadOnlyList<HistoricalBar> ToIntraday(IReadOnlyList<HistoricalBar> source, Timeframe from, Timeframe to)
    {
        var fromMinutes = Minutes(from);
        var toMinutes = Minutes(to);
        if (toMinutes <= fromMinutes || toMinutes % fromMinutes != 0)
            throw new ArgumentException($"Cannot resample {from} bars to {to}: the target must be a larger multiple of the source.");

        var result = new List<HistoricalBar>();
        var groups = source.OrderBy(b => b.Timestamp).GroupBy(b =>
        {
            var local = b.Timestamp.ToOffset(IndiaStandardTime.Offset);
            var sinceOpen = local.TimeOfDay - SessionOpen;
            var bucket = (int)Math.Floor(sinceOpen.TotalMinutes / toMinutes);
            return (Date: DateOnly.FromDateTime(local.DateTime), Bucket: bucket);
        });
        foreach (var g in groups)
        {
            var start = new DateTimeOffset(g.Key.Date.ToDateTime(TimeOnly.MinValue) + SessionOpen + TimeSpan.FromMinutes(g.Key.Bucket * toMinutes), IndiaStandardTime.Offset);
            result.Add(Aggregate(g.ToList(), start, to));
        }
        return result;
    }

    public static int Minutes(Timeframe tf) => tf switch
    {
        Timeframe.M5 => 5,
        Timeframe.M15 => 15,
        Timeframe.H1 => 60,
        _ => throw new ArgumentException($"{tf} is not an intraday timeframe."),
    };

    public static bool IsIntraday(Timeframe tf) => tf is Timeframe.H1 or Timeframe.M15 or Timeframe.M5;

    private static HistoricalBar Aggregate(IReadOnlyList<HistoricalBar> bars, DateTimeOffset timestamp, Timeframe timeframe) => new()
    {
        Symbol = bars[0].Symbol,
        Timestamp = timestamp,
        Open = bars[0].Open,
        High = bars.Max(b => b.High),
        Low = bars.Min(b => b.Low),
        Close = bars[^1].Close,
        AdjustedClose = bars[^1].AdjustedClose,
        Volume = bars.Sum(b => b.Volume),
        Source = ResampledSource,
        Timeframe = timeframe,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
