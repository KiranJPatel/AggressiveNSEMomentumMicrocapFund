using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Resampling;
using TradingPlatform.Strategy.RelativeStrength;
using TradingPlatform.Strategy.Regime;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

/// <summary>
/// Performance rewrites must not change results. Each test compares the optimized code against a verbatim copy of the ORIGINAL logic on
/// randomized inputs — including the awkward ones (gaps, mismatched calendars, unsorted input, duplicates) that must take the original path.
/// </summary>
public class OptimizationEquivalenceTests
{
    private static HistoricalBar Bar(DateOnly d, decimal close, int hour = 0) => new()
    {
        Symbol = "X", Timestamp = new DateTimeOffset(d.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(5.5)), Open = close, High = close, Low = close, Close = close,
        AdjustedClose = close, Volume = 1, Source = "t", Timeframe = Timeframe.Daily,
    };

    // ---- verbatim original PriceSeriesAligner.AlignByDate ----
    private static (List<decimal>, List<decimal>) OriginalAlign(IReadOnlyList<HistoricalBar> stockBars, IReadOnlyList<HistoricalBar> benchmarkBars)
    {
        var benchmarkByDate = benchmarkBars.ToDictionary(b => DateOnly.FromDateTime(b.Timestamp.DateTime), b => b.Close);
        var stockCloses = new List<decimal>(); var benchmarkCloses = new List<decimal>();
        foreach (var bar in stockBars.OrderBy(b => b.Timestamp))
        {
            var date = DateOnly.FromDateTime(bar.Timestamp.DateTime);
            if (benchmarkByDate.TryGetValue(date, out var bc)) { stockCloses.Add(bar.Close); benchmarkCloses.Add(bc); }
        }
        return (stockCloses, benchmarkCloses);
    }

    private static List<HistoricalBar> RandomSeries(Random rng, DateOnly start, int days, double keepProbability) =>
        Enumerable.Range(0, days).Where(_ => rng.NextDouble() < keepProbability).Select(i => Bar(start.AddDays(i), 50 + rng.Next(100))).ToList();

    private static void AssertSame(IReadOnlyList<HistoricalBar> stock, IReadOnlyList<HistoricalBar> bench, string label)
    {
        var (os, ob) = OriginalAlign(stock, bench);
        var (ns, nb) = PriceSeriesAligner.AlignByDate(stock, bench);
        Assert.True(os.SequenceEqual(ns), $"{label}: stock closes differ");
        Assert.True(ob.SequenceEqual(nb), $"{label}: benchmark closes differ");
    }

    [Fact]
    public void Aligner_MatchesTheOriginal_OnRandomSortedSeries_WithGapsAndMismatchedCalendars()
    {
        var rng = new Random(11);
        for (var trial = 0; trial < 300; trial++)
        {
            var start = new DateOnly(2024, 1, 1).AddDays(rng.Next(30));
            AssertSame(RandomSeries(rng, start, rng.Next(0, 120), 0.8), RandomSeries(rng, start.AddDays(rng.Next(-5, 6)), rng.Next(0, 120), 0.85), $"trial {trial}");
        }
    }

    [Fact]
    public void Aligner_MatchesTheOriginal_OnEmptyAndNonOverlappingInputs()
    {
        var some = RandomSeries(new Random(1), new DateOnly(2024, 1, 1), 20, 1.0);
        AssertSame(new List<HistoricalBar>(), some, "empty stock");
        AssertSame(some, new List<HistoricalBar>(), "empty benchmark");
        AssertSame(some, RandomSeries(new Random(2), new DateOnly(2030, 1, 1), 20, 1.0), "no overlap");
    }

    [Fact]
    public void Aligner_MatchesTheOriginal_WhenTheStockSeriesIsUnsorted_OrHasRepeatedDates()
    {
        var rng = new Random(5);
        var bench = RandomSeries(rng, new DateOnly(2024, 1, 1), 60, 1.0);
        var stock = RandomSeries(rng, new DateOnly(2024, 1, 1), 60, 0.9);

        var shuffled = stock.OrderBy(_ => rng.Next()).ToList();
        AssertSame(shuffled, bench, "unsorted stock");

        var repeated = stock.Concat(stock.Take(5)).ToList();          // same dates twice: the original keeps both, so must the fast path's fallback
        AssertSame(repeated, bench, "repeated stock dates");
    }

    [Fact]
    public void Aligner_StillThrows_OnDuplicateBenchmarkDates_LikeTheOriginal()
    {
        var stock = RandomSeries(new Random(1), new DateOnly(2024, 1, 1), 10, 1.0);
        var dupBench = stock.Concat(stock.Take(2)).ToList();

        Assert.Throws<ArgumentException>(() => OriginalAlign(stock, dupBench));
        Assert.Throws<ArgumentException>(() => PriceSeriesAligner.AlignByDate(stock, dupBench));
    }

    [Fact]
    public void Aligner_TwoIntradayBarsOnOneDate_TakeTheOriginalPath()
    {
        var d = new DateOnly(2024, 3, 4);
        var stock = new List<HistoricalBar> { Bar(d, 10, 9), Bar(d, 11, 10), Bar(d.AddDays(1), 12, 9) };
        var bench = new List<HistoricalBar> { Bar(d, 100), Bar(d.AddDays(1), 101) };

        AssertSame(stock, bench, "intraday-style repeated dates");
    }

    // ---- weekly bias: trimmed input vs the full-history reference ----

    private static WeeklyBias ReferenceBias(IReadOnlyList<HistoricalBar> daily, MarketRegimeOptions o)
    {
        var fast = Math.Max(2, o.WeeklyFastPeriod); var slow = Math.Max(fast + 1, o.WeeklySlowPeriod); var lookback = Math.Max(1, o.WeeklySlopeLookbackWeeks);
        var weekly = BarResampler.ToWeekly(daily);                         // the whole history, no trimming
        if (weekly.Count < slow + lookback) return WeeklyBias.Unknown;
        decimal Sma(int endExclusive, int period) { decimal sum = 0; for (var i = endExclusive - period; i < endExclusive; i++) sum += weekly[i].Close; return sum / period; }
        var n = weekly.Count; var smaFast = Sma(n, fast); var smaSlow = Sma(n, slow); var before = Sma(n - lookback, fast);
        var bullish = 0; if (weekly[^1].Close > smaFast) bullish++; if (smaFast > smaSlow) bullish++; if (smaFast > before) bullish++;
        return bullish switch { 3 => WeeklyBias.Bullish, 2 => WeeklyBias.Neutral, _ => WeeklyBias.Bearish };
    }

    [Fact]
    public void WeeklyBias_Trimmed_EqualsTheFullHistoryReference_AcrossRandomSeriesAndEndDays()
    {
        var rng = new Random(21);
        var opts = new MarketRegimeOptions();
        for (var trial = 0; trial < 120; trial++)
        {
            var days = rng.Next(150, 1400);
            var start = new DateOnly(2020, 1, 1).AddDays(rng.Next(0, 6));
            var price = 100m;
            var bars = new List<HistoricalBar>();
            for (var i = 0; i < days; i++)
            {
                var d = start.AddDays(i);
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                price = Math.Max(10m, price * (1m + (decimal)(rng.NextDouble() - 0.48) * 0.03m));
                bars.Add(Bar(d, price));
            }
            Assert.Equal(ReferenceBias(bars, opts), WeeklyMarketBiasCalculator.Calculate(bars, opts));
        }
    }

    [Fact]
    public void WeeklyBias_UnsortedInput_IsStillHandled_LikeTheReference()
    {
        var rng = new Random(4);
        var bars = Enumerable.Range(0, 300).Select(i => Bar(new DateOnly(2024, 1, 1).AddDays(i), 100 + i)).Where(b => b.Timestamp.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToList();
        var shuffled = bars.OrderBy(_ => rng.Next()).ToList();

        Assert.Equal(ReferenceBias(shuffled, new MarketRegimeOptions()), WeeklyMarketBiasCalculator.Calculate(shuffled, new MarketRegimeOptions()));
    }
}
