using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Risk.Trailing;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.RiskTests;

public class TrailingStopCalculatorTests
{
    private static readonly StrategyOptions StrategyOptions = new() { TrailingBreakevenTriggerR = 1m, TrailingActiveTriggerR = 2m, TrailingAggressiveTriggerR = 3m };
    private static readonly ExitOptions ExitOptions = new() { TrailingAtrPeriod = 14, TrailingAtrMultiplier = 3.0m, TrailingSwingLookback = 3 };

    private static Position OpenPosition(decimal entry = 100m, decimal initialStop = 90m, decimal currentStop = 90m, decimal riskPerShare = 10m) => new()
    {
        Symbol = "STK", Quantity = 100, AverageEntryPrice = entry, InitialStop = initialStop, CurrentStop = currentStop,
        InitialRiskAmount = riskPerShare * 100, RiskPerShare = riskPerShare, HighestStageFilled = PyramidStage.Stage1Initial,
        IsOpen = true, OpenedAt = DateTimeOffset.UtcNow.AddDays(-10),
    };

    /// <summary>A smooth, low-volatility rising series ending at the given close, long enough for ATR14 to stabilize.</summary>
    private static List<HistoricalBar> BuildRisingBars(decimal endClose, int count = 30)
    {
        var bars = new List<HistoricalBar>();
        var start = 100m;
        var step = (endClose - start) / (count - 1);
        for (var i = 0; i < count; i++)
        {
            var close = start + step * i;
            bars.Add(new HistoricalBar
            {
                Symbol = "STK", Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(i),
                Open = close, High = close + 1m, Low = close - 1m, Close = close, AdjustedClose = close,
                Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily
            });
        }
        return bars;
    }

    private static IndicatorSnapshot Snapshot(decimal? supertrend = null) => new()
    {
        Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily, Supertrend = supertrend
    };

    [Fact]
    public void BelowBreakevenTrigger_StageIsInitial_StopUnchanged()
    {
        var position = OpenPosition(entry: 100m, currentStop: 90m, riskPerShare: 10m);
        var bars = BuildRisingBars(endClose: 105m); // R = (105-100)/10 = 0.5, below 1R trigger

        var result = TrailingStopCalculator.Calculate(position, bars, Snapshot(), StrategyOptions, ExitOptions);

        Assert.Equal(TrailingStage.Initial, result.Stage);
        Assert.Equal(90m, result.NewStop);
    }

    [Fact]
    public void AtBreakevenTrigger_MovesStopToEntry()
    {
        var position = OpenPosition(entry: 100m, currentStop: 90m, riskPerShare: 10m);
        var bars = BuildRisingBars(endClose: 111m); // R = 1.1, in [1,2) breakeven band

        var result = TrailingStopCalculator.Calculate(position, bars, Snapshot(), StrategyOptions, ExitOptions);

        Assert.Equal(TrailingStage.Breakeven, result.Stage);
        Assert.Equal(100m, result.NewStop); // moved to entry
    }

    [Fact]
    public void AtActiveTrigger_UsesAtrBasedTrailing()
    {
        var position = OpenPosition(entry: 100m, currentStop: 90m, riskPerShare: 10m);
        var bars = BuildRisingBars(endClose: 125m); // R = 2.5, in [2,3) ATR trailing band

        var result = TrailingStopCalculator.Calculate(position, bars, Snapshot(), StrategyOptions, ExitOptions);

        Assert.Equal(TrailingStage.AtrTrailing, result.Stage);
        Assert.True(result.NewStop > 100m, "ATR-trailing stop should have moved up past breakeven.");
        Assert.True(result.NewStop < 125m, "ATR-trailing stop should still be below current price.");
    }

    [Fact]
    public void AtAggressiveTrigger_UsesTightestOfAvailableCandidates()
    {
        var position = OpenPosition(entry: 100m, currentStop: 90m, riskPerShare: 10m);
        var bars = BuildRisingBars(endClose: 140m); // R = 4.0, aggressive tier
        var indicators = Snapshot(supertrend: 135m); // a very tight supertrend candidate

        var result = TrailingStopCalculator.Calculate(position, bars, indicators, StrategyOptions, ExitOptions);

        Assert.Equal(TrailingStage.AggressiveWinnerProtection, result.Stage);
        Assert.Equal(135m, result.NewStop); // supertrend (135) should win over a much wider ATR-based candidate
    }

    [Fact]
    public void StopNeverRatchetsDown_EvenIfCandidateIsLower()
    {
        // CurrentStop is already ABOVE where a fresh breakeven calculation would place it.
        var position = OpenPosition(entry: 100m, currentStop: 108m, riskPerShare: 10m);
        var bars = BuildRisingBars(endClose: 111m); // R=1.1 -> breakeven candidate = 100, but CurrentStop (108) is already higher

        var result = TrailingStopCalculator.Calculate(position, bars, Snapshot(), StrategyOptions, ExitOptions);

        Assert.Equal(108m, result.NewStop); // must stay at 108, not drop to 100
    }

    [Fact]
    public void RMultiple_IsComputedCorrectly()
    {
        var position = OpenPosition(entry: 100m, riskPerShare: 10m);
        var bars = BuildRisingBars(endClose: 130m); // (130-100)/10 = 3.0

        var result = TrailingStopCalculator.Calculate(position, bars, Snapshot(), StrategyOptions, ExitOptions);

        Assert.Equal(3.0m, result.RMultiple);
    }

    [Fact]
    public void ZeroRiskPerShare_Throws()
    {
        var position = OpenPosition(riskPerShare: 0m);
        var bars = BuildRisingBars(endClose: 110m);
        Assert.Throws<ArgumentException>(() => TrailingStopCalculator.Calculate(position, bars, Snapshot(), StrategyOptions, ExitOptions));
    }

    [Fact]
    public void EmptyBars_Throws()
    {
        var position = OpenPosition();
        Assert.Throws<ArgumentException>(() => TrailingStopCalculator.Calculate(position, Array.Empty<HistoricalBar>(), Snapshot(), StrategyOptions, ExitOptions));
    }
}
