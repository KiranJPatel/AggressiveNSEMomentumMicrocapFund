using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Structure;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

/// <summary>Spec §3.4 price structure: HH/HL/LH/LL state and nearest confirmed support / resistance (no look-ahead).</summary>
public class PriceStructureAnalyzerTests
{
    private static HistoricalBar Bar(int i, decimal high, decimal low) => new()
    {
        Symbol = "X", Timestamp = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i),
        Open = (high + low) / 2, High = high, Low = low, Close = (high + low) / 2, AdjustedClose = (high + low) / 2, Volume = 1000, Source = "t", Timeframe = Timeframe.Daily,
    };

    /// <summary>Piecewise-linear price path through the given turning points (value at index), 1 day per bar; each bar has a +/-0.5 range.</summary>
    private static List<HistoricalBar> Path(params (int Index, decimal Value)[] turns)
    {
        var bars = new List<HistoricalBar>();
        for (var t = 0; t < turns.Length - 1; t++)
        {
            var (i0, v0) = turns[t];
            var (i1, v1) = turns[t + 1];
            for (var i = i0; i < i1; i++)
            {
                var v = v0 + (v1 - v0) * (i - i0) / (i1 - i0);
                bars.Add(Bar(i, v + 0.5m, v - 0.5m));
            }
        }
        var last = turns[^1];
        bars.Add(Bar(last.Index, last.Value + 0.5m, last.Value - 0.5m));
        return bars;
    }

    [Fact]
    public void RisingHighsAndRisingLows_IsAnUptrend_WithSupportAndResistanceFromTheNearestSwings()
    {
        // lows 100 -> 106 -> 112 (HL), highs 110 -> 118 -> 126 (HH), then price back down to 120 so there is a level on each side.
        var bars = Path((0, 105m), (5, 100m), (10, 110m), (15, 106m), (20, 118m), (25, 112m), (30, 126m), (35, 120m), (40, 122m), (45, 123m), (50, 124m));

        var s = PriceStructureAnalyzer.Analyze(bars, close: 121m, swingLookback: 3);

        Assert.Equal(PriceStructureState.Uptrend, s.State);
        Assert.Equal("HH", s.LastHighLabel);
        Assert.Equal("HL", s.LastLowLabel);
        Assert.Equal(120m - 0.5m, s.Support);       // the pullback to 120 (bar 35) is itself a confirmed swing low, and the highest one below 121
        Assert.Equal(126m + 0.5m, s.Resistance);    // lowest confirmed swing high above 121
    }

    [Fact]
    public void FallingHighsAndFallingLows_IsADowntrend()
    {
        var bars = Path((0, 125m), (5, 130m), (10, 120m), (15, 124m), (20, 112m), (25, 116m), (30, 104m), (35, 108m), (40, 106m), (45, 105m), (50, 104m));

        var s = PriceStructureAnalyzer.Analyze(bars, close: 106m, swingLookback: 3);

        Assert.Equal(PriceStructureState.Downtrend, s.State);
        Assert.Equal("LH", s.LastHighLabel);
        Assert.Equal("LL", s.LastLowLabel);
    }

    [Fact]
    public void HigherHighButLowerLow_IsMixed_NotATrend()
    {
        // highs 110 -> 118 (HH); lows 104 -> 98 (LL). After the 118 high price falls steadily, so no later swing low is confirmed
        // and the latest labels are exactly HH (high) and LL (low): the disagreement that defines "Mixed".
        var bars = Path((0, 108m), (5, 104m), (10, 110m), (15, 98m), (20, 118m), (26, 106m));

        var s = PriceStructureAnalyzer.Analyze(bars, close: 106m, swingLookback: 3);
        Assert.Equal("HH", s.LastHighLabel);
        Assert.Equal("LL", s.LastLowLabel);

        Assert.Equal(PriceStructureState.Mixed, s.State);
    }

    [Fact]
    public void TooFewSwings_IsInsufficient_AndEmptyInputDoesNotThrow()
    {
        var short1 = Path((0, 100m), (5, 105m), (10, 100m));
        Assert.Equal(PriceStructureState.Insufficient, PriceStructureAnalyzer.Analyze(short1, 100m, 3).State);
        Assert.Equal(PriceStructureState.Insufficient, PriceStructureAnalyzer.Analyze(new List<HistoricalBar>(), 100m, 3).State);
    }

    [Fact]
    public void PriceAtNewHighs_HasNoOverheadResistance()
    {
        var bars = Path((0, 105m), (5, 100m), (10, 110m), (15, 106m), (20, 118m), (25, 112m), (30, 126m), (35, 120m), (40, 135m));

        var s = PriceStructureAnalyzer.Analyze(bars, close: 135m, swingLookback: 3);

        Assert.Null(s.Resistance);
        Assert.True(s.Support is not null);
    }

    [Fact]
    public void SwingsOlderThanTheWindow_AreNotPresentedAsLiveLevels()
    {
        var bars = Path((0, 105m), (5, 100m), (10, 110m), (15, 106m), (20, 118m), (25, 112m), (30, 126m), (35, 120m), (40, 122m), (45, 123m), (50, 124m));

        var windowed = PriceStructureAnalyzer.Analyze(bars, close: 121m, swingLookback: 3, windowBars: 12);

        Assert.Null(windowed.Resistance);   // the 126 swing high is at bar ~30, outside the last 12 bars
    }
}
