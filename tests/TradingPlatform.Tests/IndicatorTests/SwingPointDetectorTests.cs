using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Indicators.Structure;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.IndicatorTests;

public class SwingPointDetectorTests
{
    // Hand-designed zigzag: peak1(15) -> trough1(Low=7) -> peak2(18, a Higher High)
    // -> trough2(Low=5, a Lower Low), with two trailing bars added purely so
    // the final trough has enough future bars to be confirmable (see
    // SwingPointDetector's no-look-ahead class remarks: a swing point can't
    // be confirmed without `lookback` bars after it).
    private static readonly decimal[] Highs = { 10, 11, 12, 15, 12, 11, 9, 11, 13, 18, 14, 12, 7, 9, 11 };

    private static IReadOnlyList<HistoricalBar> BuildBars()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5));
        var bars = new List<HistoricalBar>();
        for (var i = 0; i < Highs.Length; i++)
        {
            bars.Add(new HistoricalBar
            {
                Symbol = "ZIGZAG", Timestamp = start.AddDays(i),
                Open = Highs[i] - 1, High = Highs[i], Low = Highs[i] - 2, Close = Highs[i] - 1,
                AdjustedClose = Highs[i] - 1, Volume = 10000, Source = "Fixture", Timeframe = Timeframe.Daily
            });
        }
        return bars;
    }

    [Fact]
    public void DetectsFirstSwingHigh_WithNullLabel()
    {
        var points = SwingPointDetector.Detect(BuildBars(), lookback: 2);
        var first = points.Single(p => p.Type == SwingPointType.High && p.BarIndex == 3);
        Assert.Equal(15m, first.Price);
        Assert.True(first.StructureLabel is null, "First swing high should have no comparison label.");
    }

    [Fact]
    public void SecondSwingHigh_HigherThanFirst_LabeledHH()
    {
        var points = SwingPointDetector.Detect(BuildBars(), lookback: 2);
        var second = points.Single(p => p.Type == SwingPointType.High && p.BarIndex == 9);
        Assert.Equal(18m, second.Price);
        Assert.Equal("HH", second.StructureLabel);
    }

    [Fact]
    public void FirstSwingLow_WithNullLabel()
    {
        var points = SwingPointDetector.Detect(BuildBars(), lookback: 2);
        var first = points.Single(p => p.Type == SwingPointType.Low && p.BarIndex == 6);
        Assert.Equal(7m, first.Price);
        Assert.True(first.StructureLabel is null, "First swing low should have no comparison label.");
    }

    [Fact]
    public void SecondSwingLow_LowerThanFirst_LabeledLL()
    {
        var points = SwingPointDetector.Detect(BuildBars(), lookback: 2);
        var second = points.Single(p => p.Type == SwingPointType.Low && p.BarIndex == 12);
        Assert.Equal(5m, second.Price);
        Assert.Equal("LL", second.StructureLabel);
    }

    [Fact]
    public void TrailingEdgeWithoutEnoughFutureBars_IsNotConfirmed_NoLookAhead()
    {
        // The very last bar can never be a confirmed swing point — there's
        // no future data to confirm it against. This is the deliberate
        // no-look-ahead property, not a bug.
        var bars = BuildBars();
        var points = SwingPointDetector.Detect(bars, lookback: 2);
        Assert.True(points.All(p => p.BarIndex < bars.Count - 2), "No swing point should be reported within `lookback` bars of the series end.");
    }

    [Fact]
    public void EmptyOrFlatSeries_ProducesNoSwingPoints()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5));
        var flatBars = Enumerable.Range(0, 10).Select(i => new HistoricalBar
        {
            Symbol = "FLAT", Timestamp = start.AddDays(i),
            Open = 100, High = 100, Low = 100, Close = 100, AdjustedClose = 100,
            Volume = 1000, Source = "Fixture", Timeframe = Timeframe.Daily
        }).ToList();

        var points = SwingPointDetector.Detect(flatBars, lookback: 2);
        Assert.Equal(0, points.Count);
    }
}
