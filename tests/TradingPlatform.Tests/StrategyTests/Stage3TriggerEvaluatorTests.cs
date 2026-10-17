using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Entry;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class Stage3TriggerEvaluatorTests
{
    private static readonly StrategyOptions Options = new() { Stage3MinRetestPullbackPercent = 3m, BreakoutMinAdx = 20m };
    private static readonly DateTimeOffset Stage2Timestamp = new(2026, 1, 10, 0, 0, 0, TimeSpan.FromHours(5.5));

    private static StagedPositionState Stage2Position(decimal stage2Entry = 103m, decimal stop = 95m) => new()
    {
        Symbol = "STK", HighestStageFilled = PyramidStage.Stage2Add, CurrentStop = stop,
        Stage1EntryPrice = 100m, Stage1EntryTimestamp = Stage2Timestamp.AddDays(-5),
        Stage2EntryPrice = stage2Entry, Stage2EntryTimestamp = Stage2Timestamp,
    };

    private static HistoricalBar Bar(int dayOffset, decimal high, decimal low, decimal close) => new()
    {
        Symbol = "STK", Timestamp = Stage2Timestamp.AddDays(dayOffset), Open = close, High = high, Low = low, Close = close,
        AdjustedClose = close, Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily
    };

    private static IndicatorSnapshot GoodMomentum(decimal hmaFast = 104m, decimal? adx = 25m) => new()
    {
        Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily, HmaFast = hmaFast, Adx = adx
    };

    /// <summary>A clean retest: rallies to 112 (high), pulls back to 105 (a ~6.25% pullback, above stop), then closes back above Stage2 entry at 106.</summary>
    private static List<HistoricalBar> CleanRetestBars() => new()
    {
        Bar(0, high: 108, low: 103, close: 107),
        Bar(1, high: 112, low: 110, close: 111), // post-stage2 high = 112
        Bar(2, high: 109, low: 105, close: 106), // pullback low = 105 (>stop 95, holds)
        Bar(3, high: 107, low: 105.5m, close: 106.5m), // reclaim: close > stage2 entry (103)
    };

    [Fact]
    public void CleanRetest_AllConditionsPass_Triggers()
    {
        var result = Stage3TriggerEvaluator.Evaluate(Stage2Position(), CleanRetestBars(), GoodMomentum(), Options);
        Assert.True(result.ShouldTrigger);
    }

    [Fact]
    public void NoPullbackAtAll_StraightContinuation_DoesNotTrigger()
    {
        // Price only goes up since Stage 2 entry — never actually retests anything.
        var bars = new List<HistoricalBar>
        {
            Bar(0, high: 105, low: 103, close: 104),
            Bar(1, high: 108, low: 105, close: 107),
            Bar(2, high: 111, low: 108, close: 110),
        };
        var result = Stage3TriggerEvaluator.Evaluate(Stage2Position(), bars, GoodMomentum(), Options);
        Assert.False(result.ShouldTrigger);
        Assert.False(result.Conditions.Single(c => c.Name == "RetestOccurred").Passed);
    }

    [Fact]
    public void PullbackViolatesStop_SupportDidNotHold_DoesNotTrigger()
    {
        var bars = new List<HistoricalBar>
        {
            Bar(0, high: 112, low: 110, close: 111),
            Bar(1, high: 108, low: 94, close: 96),  // low of 94 breaches stop=95
            Bar(2, high: 105, low: 96, close: 104),
        };
        var result = Stage3TriggerEvaluator.Evaluate(Stage2Position(stop: 95m), bars, GoodMomentum(), Options);
        Assert.False(result.ShouldTrigger);
        Assert.False(result.Conditions.Single(c => c.Name == "SupportHeld").Passed);
    }

    [Fact]
    public void PullbackButNoReclaim_StillBelowStage2Entry_DoesNotTrigger()
    {
        var bars = new List<HistoricalBar>
        {
            Bar(0, high: 112, low: 110, close: 111),
            Bar(1, high: 108, low: 100, close: 101), // pulled back, hasn't reclaimed above Stage2 entry (103)
        };
        var result = Stage3TriggerEvaluator.Evaluate(Stage2Position(stage2Entry: 103m), bars, GoodMomentum(), Options);
        Assert.False(result.ShouldTrigger);
        Assert.False(result.Conditions.Single(c => c.Name == "SuccessfulReclaim").Passed);
    }

    [Fact]
    public void WeakMomentum_LowAdx_DoesNotTrigger()
    {
        var result = Stage3TriggerEvaluator.Evaluate(Stage2Position(), CleanRetestBars(), GoodMomentum(adx: 12m), Options);
        Assert.False(result.ShouldTrigger);
        Assert.False(result.Conditions.Single(c => c.Name == "MomentumConstructive").Passed);
    }

    [Fact]
    public void NotYetAtStage2_DoesNotTrigger()
    {
        var stage1Position = new StagedPositionState
        {
            Symbol = "STK", HighestStageFilled = PyramidStage.Stage1Initial, CurrentStop = 90m,
            Stage1EntryPrice = 100m, Stage1EntryTimestamp = Stage2Timestamp.AddDays(-5),
        };
        var result = Stage3TriggerEvaluator.Evaluate(stage1Position, CleanRetestBars(), GoodMomentum(), Options);
        Assert.False(result.ShouldTrigger);
    }

    [Fact]
    public void NoBarsSinceStage2_DoesNotTrigger_DoesNotThrow()
    {
        var result = Stage3TriggerEvaluator.Evaluate(Stage2Position(), Array.Empty<HistoricalBar>(), GoodMomentum(), Options);
        Assert.False(result.ShouldTrigger);
    }

    [Fact]
    public void MissingAdxData_FailsClosed()
    {
        var noAdx = new IndicatorSnapshot { Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily, HmaFast = 104m, Adx = null };
        var result = Stage3TriggerEvaluator.Evaluate(Stage2Position(), CleanRetestBars(), noAdx, Options);
        Assert.False(result.ShouldTrigger);
        Assert.False(result.Conditions.Single(c => c.Name == "MomentumConstructive").Passed);
    }

    [Fact]
    public void LowBeforeThePeak_IsNotCountedAsARetest_RegressionGuard()
    {
        // Regression guard: a low occurring in a bar BEFORE the peak bar
        // must never be treated as "the pullback low" just because it's
        // numerically the window minimum — a retest can only be measured
        // FROM the peak bar onward. Here the peak is the final bar (110-112
        // range), so there's no post-peak bar left to pull back into at
        // all, even though an earlier, unrelated bar dipped to 70.
        var bars = new List<HistoricalBar>
        {
            Bar(0, high: 100, low: 70, close: 95),   // deep low, but well BEFORE the peak — must be excluded
            Bar(1, high: 105, low: 100, close: 104),
            Bar(2, high: 112, low: 110, close: 111), // the peak, and also the last bar — nothing after it to pull back
        };
        var result = Stage3TriggerEvaluator.Evaluate(Stage2Position(stop: 60m), bars, GoodMomentum(), Options);
        Assert.False(result.Conditions.Single(c => c.Name == "RetestOccurred").Passed);
    }
}
