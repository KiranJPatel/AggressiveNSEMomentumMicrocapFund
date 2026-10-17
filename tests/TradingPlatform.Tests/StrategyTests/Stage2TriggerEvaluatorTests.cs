using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Entry;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class Stage2TriggerEvaluatorTests
{
    private static readonly StrategyOptions Options = new() { Stage2TriggerFavorableMoveMinPercent = 2m, Stage2TriggerFavorableMoveMaxPercent = 3m };

    private static StagedPositionState Stage1Position(decimal entryPrice = 100m, decimal stop = 90m) => new()
    {
        Symbol = "STK", HighestStageFilled = PyramidStage.Stage1Initial, CurrentStop = stop,
        Stage1EntryPrice = entryPrice, Stage1EntryTimestamp = DateTimeOffset.UtcNow.AddDays(-5),
    };

    private static HistoricalBar Bar(decimal close, decimal low) => new()
    {
        Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Open = close, High = close + 1, Low = low, Close = close,
        AdjustedClose = close, Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily
    };

    private static IndicatorSnapshot GoodTrend() => new()
    {
        Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily, HmaFast = 105m, HmaSlow = 100m
    };

    [Fact]
    public void AllConditionsPass_Triggers()
    {
        var result = Stage2TriggerEvaluator.Evaluate(Stage1Position(), Bar(close: 103m, low: 101m), GoodTrend(), Options);
        Assert.True(result.ShouldTrigger);
    }

    [Fact]
    public void BelowMinFavorableMove_DoesNotTrigger()
    {
        var result = Stage2TriggerEvaluator.Evaluate(Stage1Position(), Bar(close: 101m, low: 100.5m), GoodTrend(), Options); // +1%, below 2% min
        Assert.False(result.ShouldTrigger);
        Assert.False(result.Conditions.Single(c => c.Name == "FavorableMove").Passed);
    }

    [Fact]
    public void StrongerMoveBeyondMax_StillTriggers_NotBlockedByUpperCeiling()
    {
        // +8% is well beyond the 2-3% "typical window" — must still trigger, per the documented resolved ambiguity.
        var result = Stage2TriggerEvaluator.Evaluate(Stage1Position(), Bar(close: 108m, low: 106m), GoodTrend(), Options);
        Assert.True(result.ShouldTrigger);
    }

    [Fact]
    public void TrendNotValid_DoesNotTrigger()
    {
        var badTrend = new IndicatorSnapshot { Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily, HmaFast = 95m, HmaSlow = 100m };
        var result = Stage2TriggerEvaluator.Evaluate(Stage1Position(), Bar(close: 103m, low: 101m), badTrend, Options);
        Assert.False(result.ShouldTrigger);
        Assert.False(result.Conditions.Single(c => c.Name == "TrendStillValid").Passed);
    }

    [Fact]
    public void StopViolated_DoesNotTrigger()
    {
        var result = Stage2TriggerEvaluator.Evaluate(Stage1Position(stop: 90m), Bar(close: 103m, low: 89m), GoodTrend(), Options);
        Assert.False(result.ShouldTrigger);
        Assert.False(result.Conditions.Single(c => c.Name == "NoStopViolation").Passed);
    }

    [Fact]
    public void CloseBelowStage1Entry_NeverAddsToLoser()
    {
        var result = Stage2TriggerEvaluator.Evaluate(Stage1Position(entryPrice: 100m), Bar(close: 99m, low: 98m), GoodTrend(), Options);
        Assert.False(result.ShouldTrigger);
        Assert.False(result.Conditions.Single(c => c.Name == "NeverAddToLoser").Passed);
    }

    [Fact]
    public void AlreadyBeyondStage1_DoesNotTrigger()
    {
        var stage2Position = new StagedPositionState
        {
            Symbol = "STK", HighestStageFilled = PyramidStage.Stage2Add, CurrentStop = 90m,
            Stage1EntryPrice = 100m, Stage1EntryTimestamp = DateTimeOffset.UtcNow.AddDays(-5),
            Stage2EntryPrice = 103m, Stage2EntryTimestamp = DateTimeOffset.UtcNow.AddDays(-2),
        };
        var result = Stage2TriggerEvaluator.Evaluate(stage2Position, Bar(close: 110m, low: 108m), GoodTrend(), Options);
        Assert.False(result.ShouldTrigger);
    }

    [Fact]
    public void MissingHmaData_FailsClosed()
    {
        var noHma = new IndicatorSnapshot { Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily };
        var result = Stage2TriggerEvaluator.Evaluate(Stage1Position(), Bar(close: 103m, low: 101m), noHma, Options);
        Assert.False(result.ShouldTrigger);
        Assert.False(result.Conditions.Single(c => c.Name == "TrendStillValid").Passed);
    }
}
