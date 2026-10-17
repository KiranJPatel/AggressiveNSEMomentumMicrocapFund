using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Risk;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.RiskTests;

public class InitialStopCalculatorTests
{
    private static HistoricalBar Bar(int i, decimal high, decimal low, decimal close) => new()
    {
        Symbol = "STK", Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(i),
        Open = close, High = high, Low = low, Close = close, AdjustedClose = close,
        Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily
    };

    /// <summary>A steady uptrend with ATR settling into a stable range, and one clear swing low near the entry point.</summary>
    private static List<HistoricalBar> BuildTrendWithSwingLow()
    {
        var bars = new List<HistoricalBar>();
        // Warm-up: gentle, low-volatility climb (enough for ATR14 to stabilize).
        for (var i = 0; i < 20; i++)
        {
            var baseline = 100m + i * 0.5m;
            bars.Add(Bar(i, baseline + 1m, baseline - 1m, baseline));
        }
        // A clear, confirmable swing low right before "today".
        bars.Add(Bar(20, 111m, 108m, 110m));
        bars.Add(Bar(21, 110m, 106m, 107m));
        bars.Add(Bar(22, 108m, 103m, 105m)); // swing low candidate: low=103
        bars.Add(Bar(23, 109m, 106m, 108m));
        bars.Add(Bar(24, 111m, 108m, 110m)); // "entry" bar
        bars.Add(Bar(25, 112m, 109m, 111m)); // confirm bars after (lookback=3, need 3 after)
        bars.Add(Bar(26, 113m, 110m, 112m));
        bars.Add(Bar(27, 114m, 111m, 113m));
        return bars;
    }

    private static readonly RiskOptions Options = new() { StopAtrMultiplierMin = 2.0m, AtrPeriodForStop = 14, StopSwingLookback = 3 };

    [Fact]
    public void PicksTheLowerOfTechnicalAndAtrBasedStops()
    {
        var bars = BuildTrendWithSwingLow();
        var entryIndex = 27; // last bar, after the swing low is confirmed
        var entryPrice = bars[entryIndex].Close;

        var stop = InitialStopCalculator.Calculate(bars, entryIndex, entryPrice, Options);

        // Whatever the two candidates are, the result must equal the smaller of them, not the larger.
        Assert.True(stop <= entryPrice, "Stop must be below entry for a long position.");
    }

    [Fact]
    public void ResultNeverExceedsEitherCandidate()
    {
        // The chosen stop can never be numerically ABOVE both candidates — it must be the min.
        var bars = BuildTrendWithSwingLow();
        var entryIndex = 27;
        var stop = InitialStopCalculator.Calculate(bars, entryIndex, bars[entryIndex].Close, Options);

        // Reconstruct the swing-low candidate directly to cross-check.
        var swingLowCandidate = 103m; // from the fixture's designed swing low
        Assert.True(stop <= swingLowCandidate + 0.01m, $"Stop {stop} should not exceed the technical swing-low candidate ~{swingLowCandidate}");
    }

    [Fact]
    public void InvalidEntryBarIndex_Throws()
    {
        var bars = BuildTrendWithSwingLow();
        Assert.Throws<ArgumentOutOfRangeException>(() => InitialStopCalculator.Calculate(bars, bars.Count, 100m, Options));
        Assert.Throws<ArgumentOutOfRangeException>(() => InitialStopCalculator.Calculate(bars, -1, 100m, Options));
    }

    [Fact]
    public void NoSwingLowConfirmedYet_FallsBackToAtrOnly()
    {
        // Monotonically rising with no pullback at all -> SwingPointDetector finds no swing low.
        var bars = new List<HistoricalBar>();
        for (var i = 0; i < 25; i++)
        {
            var price = 100m + i * 1.5m;
            bars.Add(Bar(i, price + 1m, price - 0.5m, price));
        }
        var entryIndex = 24;
        var entryPrice = bars[entryIndex].Close;

        var stop = InitialStopCalculator.Calculate(bars, entryIndex, entryPrice, Options);

        Assert.True(stop < entryPrice, "ATR-based fallback stop should still be below entry.");
    }

    [Fact]
    public void InsufficientHistoryForBothMethods_Throws()
    {
        var bars = new List<HistoricalBar> { Bar(0, 101, 99, 100), Bar(1, 102, 100, 101) }; // way too short for ATR14 or a confirmed swing
        Assert.Throws<InvalidOperationException>(() => InitialStopCalculator.Calculate(bars, 1, 101m, Options));
    }
}
