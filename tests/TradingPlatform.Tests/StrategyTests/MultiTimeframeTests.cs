using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Resampling;
using TradingPlatform.Strategy.Regime;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

/// <summary>Spec §7.3 multi-timeframe: resampling (daily -> weekly, intraday -> coarser intraday) and the weekly market bias.</summary>
public class MultiTimeframeTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private static HistoricalBar Daily(DateOnly d, decimal o, decimal h, decimal l, decimal c, long v) => new()
    {
        Symbol = "X", Timestamp = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), Ist), Open = o, High = h, Low = l, Close = c, AdjustedClose = c,
        Volume = v, Source = "t", Timeframe = Timeframe.Daily,
    };

    private static HistoricalBar Intra(DateOnly d, int h, int m, decimal o, decimal hi, decimal lo, decimal c, long v, Timeframe tf) => new()
    {
        Symbol = "X", Timestamp = new DateTimeOffset(d.ToDateTime(new TimeOnly(h, m)), Ist), Open = o, High = hi, Low = lo, Close = c, AdjustedClose = c,
        Volume = v, Source = "t", Timeframe = tf,
    };

    // ---------------- weekly resampling ----------------

    [Fact]
    public void ToWeekly_AggregatesOhlcv_AndStampsTheWeekWithItsLastSession()
    {
        var mon = new DateOnly(2026, 10, 5);   // Monday
        var bars = new[]
        {
            Daily(mon, 100, 105, 99, 104, 1000), Daily(mon.AddDays(1), 104, 110, 103, 108, 2000), Daily(mon.AddDays(4), 108, 109, 95, 97, 3000),
            Daily(mon.AddDays(7), 97, 100, 96, 99, 500),   // next ISO week
        };

        var weekly = BarResampler.ToWeekly(bars);

        Assert.Equal(2, weekly.Count);
        var w = weekly[0];
        Assert.Equal(100m, w.Open); Assert.Equal(110m, w.High); Assert.Equal(95m, w.Low); Assert.Equal(97m, w.Close);
        Assert.Equal(6000L, w.Volume);
        Assert.Equal(Timeframe.Weekly, w.Timeframe);
        Assert.Equal(mon.AddDays(4), DateOnly.FromDateTime(w.Timestamp.DateTime));   // the Friday session
        Assert.Equal(99m, weekly[1].Close);
    }

    [Fact]
    public void ToWeekly_TreatsTheIsoWeekSpanningNewYearAsOneWeek()
    {
        // Mon 29 Dec 2025 .. Fri 2 Jan 2026 is ISO week 1 of 2026.
        var bars = new[] { Daily(new DateOnly(2025, 12, 29), 10, 11, 9, 10, 1), Daily(new DateOnly(2025, 12, 31), 10, 12, 9, 11, 1), Daily(new DateOnly(2026, 1, 2), 11, 13, 10, 12, 1) };

        Assert.Single(BarResampler.ToWeekly(bars));
    }

    [Fact]
    public void ToWeekly_IsPointInTime_AShorterSeriesNeverChangesEarlierWeeks()
    {
        var start = new DateOnly(2026, 1, 5);
        var all = Enumerable.Range(0, 60).Where(i => (start.AddDays(i).DayOfWeek) is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .Select(i => Daily(start.AddDays(i), 100 + i, 101 + i, 99 + i, 100 + i, 10)).ToList();

        var full = BarResampler.ToWeekly(all);
        var truncated = BarResampler.ToWeekly(all.Take(23).ToList());

        for (var i = 0; i < truncated.Count - 1; i++) Assert.Equal(full[i].Close, truncated[i].Close);   // completed weeks identical
        Assert.True(BarResampler.ToWeekly(Array.Empty<HistoricalBar>()).Count == 0);
    }

    // ---------------- intraday resampling ----------------

    [Fact]
    public void FiveMinuteToHourly_AnchorsAtSessionOpen_AndTheLastBucketIsShort()
    {
        var d = new DateOnly(2026, 10, 7);
        var bars = new List<HistoricalBar>();
        // 09:15..10:10 (12 five-minute bars) -> one full 09:15 hour; then 15:15, 15:20, 15:25 -> the short 15:15 bucket.
        for (var i = 0; i < 12; i++) { var t = new TimeSpan(9, 15, 0) + TimeSpan.FromMinutes(5 * i); bars.Add(Intra(d, t.Hours, t.Minutes, 100 + i, 101 + i, 99 + i, 100.5m + i, 10, Timeframe.M5)); }
        foreach (var (h, m) in new[] { (15, 15), (15, 20), (15, 25) }) bars.Add(Intra(d, h, m, 200, 201, 199, 200, 5, Timeframe.M5));

        var hourly = BarResampler.ToIntraday(bars, Timeframe.M5, Timeframe.H1);

        Assert.Equal(2, hourly.Count);
        Assert.Equal(new TimeSpan(9, 15, 0), hourly[0].Timestamp.TimeOfDay);
        Assert.Equal(100m, hourly[0].Open);              // first 5m bar's open
        Assert.Equal(111.5m, hourly[0].Close);           // last 5m bar's close
        Assert.Equal(120L, hourly[0].Volume);
        Assert.Equal(new TimeSpan(15, 15, 0), hourly[1].Timestamp.TimeOfDay);
        Assert.Equal(15L, hourly[1].Volume);
        Assert.Equal(Timeframe.H1, hourly[0].Timeframe);
    }

    [Fact]
    public void QuarterHourToHourly_FoldsFourBarsIntoOne()
    {
        var d = new DateOnly(2026, 10, 7);
        var bars = Enumerable.Range(0, 4).Select(i => { var t = new TimeSpan(10, 15, 0) + TimeSpan.FromMinutes(15 * i); return Intra(d, t.Hours, t.Minutes, 50 + i, 52 + i, 49, 51 + i, 100, Timeframe.M15); }).ToList();

        var hourly = BarResampler.ToIntraday(bars, Timeframe.M15, Timeframe.H1);

        var h = Assert.Single(hourly);
        Assert.Equal(new TimeSpan(10, 15, 0), h.Timestamp.TimeOfDay);
        Assert.Equal(400L, h.Volume);
        Assert.Equal(54m, h.Close);
    }

    [Fact]
    public void Resampling_RejectsNonMultiplesAndNonIntradayTimeframes()
    {
        var one = new List<HistoricalBar> { Intra(new DateOnly(2026, 10, 7), 9, 15, 1, 1, 1, 1, 1, Timeframe.M15) };

        Assert.Throws<ArgumentException>(() => BarResampler.ToIntraday(one, Timeframe.M15, Timeframe.M5));      // finer than the source
        Assert.Throws<ArgumentException>(() => BarResampler.ToIntraday(one, Timeframe.M15, Timeframe.M15));     // same
        Assert.Throws<ArgumentException>(() => BarResampler.ToIntraday(one, Timeframe.Daily, Timeframe.H1));    // not intraday
    }

    // ---------------- weekly market bias ----------------

    /// <summary>Five sessions per week at the given weekly close, Monday-first.</summary>
    private static List<HistoricalBar> Weeks(IReadOnlyList<decimal> weeklyCloses)
    {
        var bars = new List<HistoricalBar>();
        var monday = new DateOnly(2025, 1, 6);
        for (var w = 0; w < weeklyCloses.Count; w++)
            for (var d = 0; d < 5; d++) bars.Add(Daily(monday.AddDays(w * 7 + d), weeklyCloses[w], weeklyCloses[w] + 1, weeklyCloses[w] - 1, weeklyCloses[w], 1000));
        return bars;
    }

    [Fact]
    public void SteadyRise_IsBullish()
    {
        Assert.Equal(WeeklyBias.Bullish, WeeklyMarketBiasCalculator.Calculate(Weeks(Enumerable.Range(0, 45).Select(i => 100m + i * 2).ToList()), new MarketRegimeOptions()));
    }

    [Fact]
    public void SteadyFall_IsBearish()
    {
        Assert.Equal(WeeklyBias.Bearish, WeeklyMarketBiasCalculator.Calculate(Weeks(Enumerable.Range(0, 45).Select(i => 300m - i * 2).ToList()), new MarketRegimeOptions()));
    }

    [Fact]
    public void ShortPullbackInAnUptrend_IsNeutral_TwoOfThreeSignals()
    {
        var closes = Enumerable.Range(0, 36).Select(i => 100m + i * 2).ToList();
        closes.Add(140m); closes.Add(140m);   // two weeks well below the fast SMA, but the longer trend is intact

        Assert.Equal(WeeklyBias.Neutral, WeeklyMarketBiasCalculator.Calculate(Weeks(closes), new MarketRegimeOptions()));
    }

    [Fact]
    public void TooLittleHistory_IsUnknown_NeverBullish()
    {
        Assert.Equal(WeeklyBias.Unknown, WeeklyMarketBiasCalculator.Calculate(Weeks(Enumerable.Range(0, 20).Select(i => 100m + i * 2).ToList()), new MarketRegimeOptions()));
        Assert.Equal(WeeklyBias.Unknown, WeeklyMarketBiasCalculator.Calculate(new List<HistoricalBar>(), new MarketRegimeOptions()));
    }

    // ---------------- regime integration ----------------

    private static MarketRegimeSnapshot Regime(IReadOnlyList<HistoricalBar> bars, MarketRegimeOptions o) =>
        MarketRegimeEngine.Calculate(new MarketRegimeInputs { BroadIndexBars = bars }, o, new RsOptions(), DateOnly.FromDateTime(bars[^1].Timestamp.Date));

    [Fact]
    public void RegimeSnapshot_AlwaysRecordsTheWeeklyBias()
    {
        var bars = Weeks(Enumerable.Range(0, 60).Select(i => 100m + i * 2).ToList());

        Assert.Equal(WeeklyBias.Bullish, Regime(bars, new MarketRegimeOptions()).WeeklyBias);
    }

    [Fact]
    public void BearishCap_LowersDeployableCapital_ButByDefaultNothingChanges()
    {
        var bars = Weeks(Enumerable.Range(0, 60).Select(i => 400m - i * 3).ToList());

        var uncapped = Regime(bars, new MarketRegimeOptions());
        var capped = Regime(bars, new MarketRegimeOptions { WeeklyBearishDeployableCapPercent = 5m });

        Assert.Equal(WeeklyBias.Bearish, uncapped.WeeklyBias);
        Assert.True(uncapped.DeployableCapitalPercent > 5m, "default config must leave exposure alone");
        Assert.Equal(5m, capped.DeployableCapitalPercent);
        Assert.Equal(uncapped.RegimeScore, capped.RegimeScore);
    }

    [Fact]
    public void BearishCap_NeverRaisesExposure()
    {
        var bars = Weeks(Enumerable.Range(0, 60).Select(i => 400m - i * 3).ToList());

        var generous = Regime(bars, new MarketRegimeOptions { WeeklyBearishDeployableCapPercent = 100m });
        var baseline = Regime(bars, new MarketRegimeOptions());

        Assert.Equal(baseline.DeployableCapitalPercent, generous.DeployableCapitalPercent);
    }

    [Fact]
    public void BearishCap_DoesNotApplyWhenTheWeeklyBiasIsNotBearish()
    {
        var bars = Weeks(Enumerable.Range(0, 60).Select(i => 100m + i * 2).ToList());

        var capped = Regime(bars, new MarketRegimeOptions { WeeklyBearishDeployableCapPercent = 1m });

        Assert.True(capped.DeployableCapitalPercent > 1m);
    }
}
