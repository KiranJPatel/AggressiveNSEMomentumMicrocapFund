using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy;
using TradingPlatform.Strategy.Entry;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class StrategyStepEngineTests
{
    private static readonly StrategyOptions StrategyOptions = new() { BreakoutVolumeMultiplier = 1.5m, BreakoutMinAdx = 20m, Stage2TriggerFavorableMoveMinPercent = 2m, Stage3MinRetestPullbackPercent = 3m };
    private static readonly RsOptions RsOptions = new() { SubstantialOutperformanceScoreThreshold = 70m };

    private static HistoricalBar Bar(int dayOffset, decimal close, long volume = 100000) => new()
    {
        Symbol = "STK", Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(dayOffset),
        Open = close, High = close + 1, Low = close - 1, Close = close, AdjustedClose = close,
        Volume = volume, Source = "Fixture", Timeframe = Timeframe.Daily
    };

    private static IndicatorSnapshot GoodBreakoutIndicators(int dayOffset, decimal relativeVolume = 2.0m) => new()
    {
        Symbol = "STK", Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(dayOffset),
        Timeframe = Timeframe.Daily, RelativeVolume = relativeVolume, Adx = 25m, HmaFast = 95m, HmaSlow = 90m
    };

    private static VcpPattern Pattern(decimal pivot) => new()
    {
        Symbol = "STK", AsOfDate = new DateOnly(2026, 3, 1), VcpScore = 70m, NumberOfContractions = 3,
        ContractionRatios = new List<decimal> { 20m, 10m, 5m }, PivotPrice = pivot, BaseLengthDays = 20,
        AverageVolumeDuringBase = 100000m, VolumeDryUpScore = 60m, CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void NoPosition_NoVcpPattern_ReturnsNoSignal()
    {
        var inputs = new StrategyStepInputs
        {
            Symbol = "STK", Bars = new[] { Bar(0, 100m) }, IndicatorSnapshots = new[] { GoodBreakoutIndicators(0) },
            VcpPattern = null, RelativeStrengthScore = 80m, Regime = RegimeClassification.Normal, ExistingPosition = null,
        };
        var result = StrategyStepEngine.Evaluate(inputs, StrategyOptions, RsOptions);
        Assert.Equal(StrategyStepDecision.NoSignal, result.Decision);
    }

    [Fact]
    public void NoPosition_ValidBreakout_ReturnsEnterStage1_WithPivotAsReferencePrice()
    {
        var inputs = new StrategyStepInputs
        {
            Symbol = "STK", Bars = new[] { Bar(0, 110m) }, IndicatorSnapshots = new[] { GoodBreakoutIndicators(0) },
            VcpPattern = Pattern(pivot: 100m), RelativeStrengthScore = 80m, Regime = RegimeClassification.Normal, ExistingPosition = null,
        };
        var result = StrategyStepEngine.Evaluate(inputs, StrategyOptions, RsOptions);

        Assert.Equal(StrategyStepDecision.EnterStage1, result.Decision);
        Assert.Equal(PyramidStage.Stage1Initial, result.Stage);
        Assert.Equal(100m, result.ReferencePrice);
    }

    [Fact]
    public void NoPosition_ValidBreakout_WithTotalQuantity_ComputesStage1Quantity()
    {
        var inputs = new StrategyStepInputs
        {
            Symbol = "STK", Bars = new[] { Bar(0, 110m) }, IndicatorSnapshots = new[] { GoodBreakoutIndicators(0) },
            VcpPattern = Pattern(pivot: 100m), RelativeStrengthScore = 80m, Regime = RegimeClassification.Normal,
            ExistingPosition = null, TotalTargetQuantity = 1000m,
        };
        var result = StrategyStepEngine.Evaluate(inputs, StrategyOptions, RsOptions);

        Assert.Equal(400m, result.SuggestedQuantity); // 40% of 1000
    }

    [Fact]
    public void NoPosition_ValidBreakout_WithoutTotalQuantity_QuantityIsNull()
    {
        var inputs = new StrategyStepInputs
        {
            Symbol = "STK", Bars = new[] { Bar(0, 110m) }, IndicatorSnapshots = new[] { GoodBreakoutIndicators(0) },
            VcpPattern = Pattern(pivot: 100m), RelativeStrengthScore = 80m, Regime = RegimeClassification.Normal, ExistingPosition = null,
        };
        var result = StrategyStepEngine.Evaluate(inputs, StrategyOptions, RsOptions);
        Assert.True(result.SuggestedQuantity is null);
    }

    [Fact]
    public void NoPosition_EmergencyRegime_BlocksEntry_EvenWithGoodBreakout()
    {
        var inputs = new StrategyStepInputs
        {
            Symbol = "STK", Bars = new[] { Bar(0, 110m) }, IndicatorSnapshots = new[] { GoodBreakoutIndicators(0) },
            VcpPattern = Pattern(pivot: 100m), RelativeStrengthScore = 80m, Regime = RegimeClassification.Emergency, ExistingPosition = null,
        };
        var result = StrategyStepEngine.Evaluate(inputs, StrategyOptions, RsOptions);
        Assert.Equal(StrategyStepDecision.NoSignal, result.Decision);
    }

    [Fact]
    public void Stage1Position_FavorableMove_ReturnsAddStage2()
    {
        var position = new StagedPositionState
        {
            Symbol = "STK", HighestStageFilled = PyramidStage.Stage1Initial, CurrentStop = 90m,
            Stage1EntryPrice = 100m, Stage1EntryTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)),
        };
        var bar = new HistoricalBar { Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Open = 103m, High = 104m, Low = 102m, Close = 103m, AdjustedClose = 103m, Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily };
        var indicators = new IndicatorSnapshot { Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily, HmaFast = 102m, HmaSlow = 98m };

        var inputs = new StrategyStepInputs
        {
            Symbol = "STK", Bars = new[] { bar }, IndicatorSnapshots = new[] { indicators },
            VcpPattern = null, RelativeStrengthScore = 80m, Regime = RegimeClassification.Normal, ExistingPosition = position,
        };
        var result = StrategyStepEngine.Evaluate(inputs, StrategyOptions, RsOptions);

        Assert.Equal(StrategyStepDecision.AddStage2, result.Decision);
        Assert.Equal(PyramidStage.Stage2Add, result.Stage);
        Assert.Equal(100m, result.ReferencePrice); // Stage1 entry price
    }

    [Fact]
    public void Stage1Position_NoFavorableMoveYet_ReturnsHoldExisting()
    {
        var position = new StagedPositionState
        {
            Symbol = "STK", HighestStageFilled = PyramidStage.Stage1Initial, CurrentStop = 90m,
            Stage1EntryPrice = 100m, Stage1EntryTimestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)),
        };
        var bar = new HistoricalBar { Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Open = 100.5m, High = 101m, Low = 100m, Close = 100.5m, AdjustedClose = 100.5m, Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily };
        var indicators = new IndicatorSnapshot { Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily, HmaFast = 99m, HmaSlow = 98m };

        var inputs = new StrategyStepInputs
        {
            Symbol = "STK", Bars = new[] { bar }, IndicatorSnapshots = new[] { indicators },
            VcpPattern = null, RelativeStrengthScore = 80m, Regime = RegimeClassification.Normal, ExistingPosition = position,
        };
        var result = StrategyStepEngine.Evaluate(inputs, StrategyOptions, RsOptions);
        Assert.Equal(StrategyStepDecision.HoldExisting, result.Decision);
    }

    [Fact]
    public void Stage2Position_SlicesOnlyBarsSinceStage2Entry()
    {
        var stage2Timestamp = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.FromHours(5.5));
        var position = new StagedPositionState
        {
            Symbol = "STK", HighestStageFilled = PyramidStage.Stage2Add, CurrentStop = 90m,
            Stage1EntryPrice = 100m, Stage1EntryTimestamp = stage2Timestamp.AddDays(-5),
            Stage2EntryPrice = 103m, Stage2EntryTimestamp = stage2Timestamp,
        };

        // Bars BEFORE stage2Timestamp include a huge dip that would wrongly count toward the retest if not sliced out.
        var bars = new List<HistoricalBar>
        {
            new() { Symbol = "STK", Timestamp = stage2Timestamp.AddDays(-3), Open = 50, High = 55, Low = 10, Close = 52, AdjustedClose = 52, Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily }, // huge low=10, should be excluded
            new() { Symbol = "STK", Timestamp = stage2Timestamp, Open = 103, High = 112, Low = 102, Close = 111, AdjustedClose = 111, Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily },
            new() { Symbol = "STK", Timestamp = stage2Timestamp.AddDays(1), Open = 109, High = 110, Low = 105, Close = 106, AdjustedClose = 106, Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily },
        };
        var indicators = new IndicatorSnapshot { Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily, HmaFast = 104m, Adx = 25m };

        var inputs = new StrategyStepInputs
        {
            Symbol = "STK", Bars = bars, IndicatorSnapshots = new[] { indicators },
            VcpPattern = null, RelativeStrengthScore = 80m, Regime = RegimeClassification.Normal, ExistingPosition = position,
        };
        var result = StrategyStepEngine.Evaluate(inputs, StrategyOptions, RsOptions);

        // If the pre-stage2 bar (low=10) leaked in, SupportHeld would fail against stop=90. It shouldn't leak in.
        Assert.True(result.Decision is StrategyStepDecision.AddStage3 or StrategyStepDecision.HoldExisting);
        if (result.Decision == StrategyStepDecision.HoldExisting)
            Assert.False(result.Reasons.Any(r => r.Contains("<= stop 90") && r.Contains("10")));
    }

    [Fact]
    public void Stage3Position_FullyStagedIn_ReturnsHoldExisting()
    {
        var position = new StagedPositionState
        {
            Symbol = "STK", HighestStageFilled = PyramidStage.Stage3Add, CurrentStop = 90m,
            Stage1EntryPrice = 100m, Stage1EntryTimestamp = DateTimeOffset.UtcNow.AddDays(-10),
            Stage2EntryPrice = 103m, Stage2EntryTimestamp = DateTimeOffset.UtcNow.AddDays(-5),
        };
        var inputs = new StrategyStepInputs
        {
            Symbol = "STK", Bars = new[] { Bar(0, 120m) }, IndicatorSnapshots = new[] { GoodBreakoutIndicators(0) },
            VcpPattern = null, RelativeStrengthScore = 80m, Regime = RegimeClassification.Normal, ExistingPosition = position,
        };
        var result = StrategyStepEngine.Evaluate(inputs, StrategyOptions, RsOptions);
        Assert.Equal(StrategyStepDecision.HoldExisting, result.Decision);
    }

    [Fact]
    public void EmptyBars_Throws()
    {
        var inputs = new StrategyStepInputs
        {
            Symbol = "STK", Bars = Array.Empty<HistoricalBar>(), IndicatorSnapshots = Array.Empty<IndicatorSnapshot>(),
            VcpPattern = null, RelativeStrengthScore = 50m, Regime = RegimeClassification.Normal, ExistingPosition = null,
        };
        Assert.Throws<ArgumentException>(() => StrategyStepEngine.Evaluate(inputs, StrategyOptions, RsOptions));
    }
}
