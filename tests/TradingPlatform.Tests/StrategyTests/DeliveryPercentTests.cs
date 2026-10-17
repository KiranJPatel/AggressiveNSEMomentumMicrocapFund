using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Breakout;
using TradingPlatform.Strategy.Scoring;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

/// <summary>Spec §3.4 delivery %: parsing NSE files, the point-in-time analyzer, the optional score blend and the optional breakout gate.</summary>
public class DeliveryPercentTests
{
    private static DeliveryRecord Rec(string symbol, DateOnly d, decimal pct) => new() { Symbol = symbol, TradeDate = d, DeliveryPercent = pct };

    /// <summary>One record per consecutive date starting at <paramref name="start"/>.</summary>
    private static List<DeliveryRecord> Series(DateOnly start, params decimal[] percents) =>
        percents.Select((p, i) => Rec("X", start.AddDays(i), p)).ToList();

    private static StockScoringOptions Opts(int lookback = 5, int min = 3) => new() { DeliveryLookbackDays = lookback, DeliveryMinSampleDays = min };

    // ---------------- parser ----------------

    private const string NseFile =
        "SYMBOL, SERIES, DATE1, PREV_CLOSE, OPEN_PRICE, HIGH_PRICE, LOW_PRICE, LAST_PRICE, CLOSE_PRICE, AVG_PRICE, TTL_TRD_QNTY, TURNOVER_LACS, NO_OF_TRADES, DELIV_QTY, DELIV_PER\n" +
        "RELIANCE, EQ, 07-Oct-2026, 100, 101, 105, 99, 104, 103, 102, 1000000, 1020.5, 5000, 450000, 45.00\n" +
        "RELIANCE, BE, 07-Oct-2026, 100, 101, 105, 99, 104, 103, 102, 5000, 5.1, 50, 5000, 100.00\n" +
        "TCS, EQ, 07-Oct-2026, 100, 101, 105, 99, 104, 103, 102, 2000000, 2040, 6000, 1500000, 75.5\n" +
        "SOMEETF, EQ, 07-Oct-2026, 10, 10, 10, 10, 10, 10, 10, 100, 1, 5, -, -\n" +
        "UNLISTED, EQ, 07-Oct-2026, 10, 10, 10, 10, 10, 10, 10, 100, 1, 5, 50, 50.0\n";

    [Fact]
    public void NseBhavcopy_KeepsOnlyEqRowsWithAFigure_AndInsideTheUniverse()
    {
        var r = DeliveryCsvParser.Parse(NseFile, "sec_bhavdata_full_07102026.csv", new HashSet<string> { "RELIANCE", "TCS", "SOMEETF" });

        Assert.Equal(DeliveryFileFormat.NseBhavcopy, r.Format);
        Assert.Equal(5, r.RowsRead);
        Assert.Equal(2, r.Records.Count);
        Assert.Equal(1, r.SkippedOtherSeries);          // the BE (trade-for-trade) row
        Assert.Equal(1, r.SkippedNoDeliveryFigure);     // SOMEETF: "-"
        Assert.Equal(1, r.SkippedNotInUniverse);        // UNLISTED
        Assert.Empty(r.Rejected);
        var rel = r.Records.Single(x => x.Symbol == "RELIANCE");
        Assert.Equal(new DateOnly(2026, 10, 7), rel.TradeDate);
        Assert.Equal(45m, rel.DeliveryPercent);
        Assert.Equal(1_000_000L, rel.TotalTradedQuantity);
        Assert.Equal(450_000L, rel.DeliverableQuantity);
        Assert.Equal("sec_bhavdata_full_07102026.csv", rel.Source);
    }

    [Fact]
    public void NoUniverseFilter_KeepsEveryEqSymbol()
    {
        Assert.Equal(3, DeliveryCsvParser.Parse(NseFile, "f", null).Records.Count);   // RELIANCE, TCS, UNLISTED
    }

    [Fact]
    public void Generic_Layout_ComputesThePercentFromQuantities_WhenItIsLeftBlank()
    {
        var csv = "Symbol,TradeDate,DeliveryPercent,TradedQuantity,DeliveredQuantity\nAAA,2026-10-07,62.5,,\nBBB,2026-10-07,,2000,500\nCCC,2026-10-07,,,\n";

        var r = DeliveryCsvParser.Parse(csv, "g", null);

        Assert.Equal(DeliveryFileFormat.Generic, r.Format);
        Assert.Equal(62.5m, r.Records.Single(x => x.Symbol == "AAA").DeliveryPercent);
        Assert.Equal(25m, r.Records.Single(x => x.Symbol == "BBB").DeliveryPercent);      // 500 / 2000
        Assert.Equal(1, r.SkippedNoDeliveryFigure);                                       // CCC: nothing to go on
    }

    [Fact]
    public void BadRows_AreRejectedAndListed_WithoutStoppingTheRest()
    {
        var csv = "Symbol,TradeDate,DeliveryPercent\nGOOD,2026-10-07,40\nBADPCT,2026-10-07,abc\nRANGE,2026-10-07,140\nBADDATE,07/13/2026,50\nGOOD,2026-10-07,41\n,2026-10-07,50\n";

        var r = DeliveryCsvParser.Parse(csv, "g", null);

        Assert.Equal(1, r.Records.Count);
        Assert.Equal(5, r.Rejected.Count);
        Assert.True(r.Rejected.Any(x => x.Contains("not a number")));
        Assert.True(r.Rejected.Any(x => x.Contains("outside 0-100")));
        Assert.True(r.Rejected.Any(x => x.Contains("not dd-MMM-yyyy")));
        Assert.True(r.Rejected.Any(x => x.Contains("duplicate")));
        Assert.True(r.Rejected.Any(x => x.Contains("blank symbol")));
    }

    [Fact]
    public void UnrecognisedOrEmptyFiles_ReportAFatalError_NotAnEmptySuccess()
    {
        Assert.NotNull(DeliveryCsvParser.Parse("A,B\n1,2\n", "x", null).FatalError);
        Assert.NotNull(DeliveryCsvParser.Parse("", "x", null).FatalError);
        Assert.NotNull(DeliveryCsvParser.Parse("Symbol,TradeDate\nAAA,2026-10-07\n", "x", null).FatalError);   // no percent and no quantities
    }

    // ---------------- analyzer ----------------

    [Fact]
    public void Snapshot_IsTodayVersusTheAverageOfTheSessionsBeforeIt()
    {
        var start = new DateOnly(2026, 10, 1);
        var records = Series(start, 40, 50, 60, 30, 45, 80);    // last = 80; previous 5 average (40+50+60+30+45)/5 = 45

        var s = DeliveryAnalyzer.Evaluate(records, start.AddDays(5), Opts(lookback: 5, min: 3))!;

        Assert.Equal(80m, s.TodayPercent);
        Assert.Equal(45m, s.AveragePercent);
        Assert.Equal(80m / 45m, s.Ratio);
        Assert.Equal(5, s.SampleDays);
    }

    [Fact]
    public void Snapshot_NeedsAnExactDateMatch_NeverCarriesYesterdayForward()
    {
        var start = new DateOnly(2026, 10, 1);
        var records = Series(start, 40, 50, 60, 30, 45);

        Assert.Null(DeliveryAnalyzer.Evaluate(records, start.AddDays(7), Opts()));     // no record for that day
    }

    [Fact]
    public void Snapshot_NeedsAMinimumBaseline_ElseNull()
    {
        var start = new DateOnly(2026, 10, 1);

        Assert.Null(DeliveryAnalyzer.Evaluate(Series(start, 40, 50, 60), start.AddDays(2), Opts(min: 3)));     // only 2 earlier sessions
        Assert.NotNull(DeliveryAnalyzer.Evaluate(Series(start, 40, 50, 60, 70), start.AddDays(3), Opts(min: 3)));
    }

    [Fact]
    public void Snapshot_UsesOnlyTheLastNBaselineSessions()
    {
        var start = new DateOnly(2026, 10, 1);
        var records = Series(start, 90, 90, 10, 20, 30, 60);   // lookback 3 -> baseline is 10,20,30 (avg 20), the early 90s are ignored

        Assert.Equal(20m, DeliveryAnalyzer.Evaluate(records, start.AddDays(5), Opts(lookback: 3, min: 3))!.AveragePercent);
    }

    [Fact]
    public void Snapshot_AZeroAverage_IsNotComputed()
    {
        var start = new DateOnly(2026, 10, 1);

        Assert.Null(DeliveryAnalyzer.Evaluate(Series(start, 0, 0, 0, 50), start.AddDays(3), Opts(min: 3)));
    }

    [Fact]
    public void EvaluateSeries_MatchesEvaluate_OnEveryDate()
    {
        var start = new DateOnly(2026, 10, 1);
        var rng = new Random(3);
        var records = Series(start, Enumerable.Range(0, 40).Select(_ => (decimal)(20 + rng.Next(60))).ToArray());
        var opts = Opts(lookback: 7, min: 4);

        var all = DeliveryAnalyzer.EvaluateSeries(records, opts);

        foreach (var r in records)
        {
            var one = DeliveryAnalyzer.Evaluate(records, r.TradeDate, opts);
            if (one is null) Assert.False(all.ContainsKey(r.TradeDate), $"{r.TradeDate}: series produced a snapshot where Evaluate did not");
            else { Assert.Equal(one.Ratio, all[r.TradeDate].Ratio); Assert.Equal(one.SampleDays, all[r.TradeDate].SampleDays); }
        }
    }

    [Fact]
    public void Score_MapsTheRatioLinearly_BetweenTheConfiguredBounds()
    {
        var o = new StockScoringOptions { DeliveryRatioForZeroScore = 0.8m, DeliveryRatioForFullScore = 1.5m };

        Assert.Equal(0m, DeliveryAnalyzer.Score(new DeliverySnapshot(10, 10, 0.5m, 10), o));
        Assert.Equal(100m, DeliveryAnalyzer.Score(new DeliverySnapshot(10, 10, 2.0m, 10), o));
        Assert.Equal(50m, DeliveryAnalyzer.Score(new DeliverySnapshot(10, 10, 1.15m, 10), o));
    }

    // ---------------- scoring blend ----------------

    private static StockScore Score(DeliverySnapshot? delivery, bool use, decimal blend = 50m)
    {
        var bars = Enumerable.Range(0, 30).Select(i => new HistoricalBar
        {
            Symbol = "X", Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(i), Open = 100, High = 101, Low = 99, Close = 100,
            AdjustedClose = 100, Volume = 1000, Source = "t", Timeframe = Timeframe.Daily,
        }).ToList();
        var snaps = bars.Select(b => new IndicatorSnapshot { Symbol = "X", Timestamp = b.Timestamp, Timeframe = Timeframe.Daily, RelativeVolume = 1.0m }).ToList();
        return StockScoringEngine.Calculate(new StockScoringInputs
        {
            Symbol = "X", AsOfDate = new DateOnly(2026, 1, 30), Bars = bars, IndicatorSnapshots = snaps, RelativeStrengthScore = 50m, Delivery = delivery,
        }, new StrategyOptions(), new StockScoringOptions { UseDeliveryPercent = use, DeliveryBlendWeightPercent = blend });
    }

    [Fact]
    public void Blend_RaisesTheVolumeComponent_ForHighDelivery_AndLowersItForLow_OnlyWhenEnabled()
    {
        var high = new DeliverySnapshot(80, 40, 2.0m, 20);    // delivery score 100
        var low = new DeliverySnapshot(20, 40, 0.5m, 20);     // delivery score 0

        var off = Score(high, use: false);
        var onHigh = Score(high, use: true);
        var onLow = Score(low, use: true);

        Assert.True(onHigh.VolumeScore > off.VolumeScore, $"high delivery should lift volume score ({onHigh.VolumeScore} vs {off.VolumeScore})");
        Assert.True(onLow.VolumeScore < off.VolumeScore, $"low delivery should lower it ({onLow.VolumeScore} vs {off.VolumeScore})");
        Assert.Equal(off.VolumeScore, Score(low, use: false).VolumeScore);   // disabled: delivery has no effect whatever it says
    }

    [Fact]
    public void Blend_WithNoDeliveryData_LeavesTheVolumeScoreExactlyAsItWas()
    {
        Assert.Equal(Score(null, use: false).VolumeScore, Score(null, use: true).VolumeScore);
    }

    [Fact]
    public void Blend_Weight_IsHonouredAtItsExtremes()
    {
        var high = new DeliverySnapshot(80, 40, 2.0m, 20);

        Assert.Equal(Score(high, use: false).VolumeScore, Score(high, use: true, blend: 0m).VolumeScore);   // 0% weight = no effect
        Assert.Equal(100m, Score(high, use: true, blend: 100m).VolumeScore);                                  // 100% weight = the delivery score itself
    }

    // ---------------- breakout gate ----------------

    private static BreakoutEvaluation Breakout(decimal? minRatio, DeliverySnapshot? delivery) => BreakoutEngine.Evaluate(
        110m, 100m, new IndicatorSnapshot { Symbol = "X", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily, RelativeVolume = 2m, Adx = 25m, HmaFast = 105m, HmaSlow = 100m },
        80m, RegimeClassification.Normal, new StrategyOptions { BreakoutVolumeMultiplier = 1.5m, BreakoutMinAdx = 20m, BreakoutMinDeliveryRatio = minRatio },
        new RsOptions { SubstantialOutperformanceScoreThreshold = 70m }, delivery);

    [Fact]
    public void Gate_NotConfigured_AddsNoConditionAndChangesNothing()
    {
        var r = Breakout(null, new DeliverySnapshot(10, 40, 0.25m, 20));

        Assert.True(r.IsBreakout);
        Assert.False(r.Conditions.Any(c => c.Name == "Delivery"), "no delivery condition may be added when the gate is not configured");
    }

    [Fact]
    public void Gate_Configured_PassesOnHighDelivery_AndFailsOnLow()
    {
        Assert.True(Breakout(1.2m, new DeliverySnapshot(60, 40, 1.5m, 20)).IsBreakout);

        var weak = Breakout(1.2m, new DeliverySnapshot(30, 40, 0.75m, 20));
        Assert.False(weak.IsBreakout);
        Assert.False(weak.Conditions.Single(c => c.Name == "Delivery").Passed);
    }

    [Fact]
    public void Gate_Configured_WithNoDeliveryData_FailsClosed()
    {
        var r = Breakout(1.2m, null);

        Assert.False(r.IsBreakout);
        Assert.True(r.Conditions.Single(c => c.Name == "Delivery").Detail.Contains("fails closed"));
    }
}
