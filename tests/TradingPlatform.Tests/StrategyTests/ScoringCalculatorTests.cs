using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Scoring;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class ScoringCalculatorTests
{
    private static HistoricalBar Bar(int dayOffset, decimal close) => new()
    {
        Symbol = "STK", Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)).AddDays(dayOffset),
        Open = close, High = close, Low = close, Close = close, AdjustedClose = close,
        Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily
    };

    private static IndicatorSnapshot Snapshot(decimal? relativeVolume = null, decimal? hmaFast = null, decimal? hmaSlow = null,
        decimal? sma50 = null, decimal? sma200 = null, decimal? supertrend = null, decimal? adx = null) => new()
    {
        Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily,
        RelativeVolume = relativeVolume, HmaFast = hmaFast, HmaSlow = hmaSlow,
        Sma50 = sma50, Sma200 = sma200, Supertrend = supertrend, Adx = adx
    };

    // --- VolumeAccumulationCalculator ---

    [Fact]
    public void VolumeAccumulation_AllAboveAverage_ScoresAboveFifty()
    {
        var snapshots = Enumerable.Range(0, 10).Select(_ => Snapshot(relativeVolume: 1.5m)).ToList();
        var score = VolumeAccumulationCalculator.Calculate(snapshots, new StockScoringOptions());
        Assert.True(score > 50m);
    }

    [Fact]
    public void VolumeAccumulation_ExactlyAverage_ScoresFifty()
    {
        var snapshots = Enumerable.Range(0, 10).Select(_ => Snapshot(relativeVolume: 1.0m)).ToList();
        var score = VolumeAccumulationCalculator.Calculate(snapshots, new StockScoringOptions());
        Assert.Equal(50m, score);
    }

    [Fact]
    public void VolumeAccumulation_BelowAverage_ScoresBelowFifty()
    {
        var snapshots = Enumerable.Range(0, 10).Select(_ => Snapshot(relativeVolume: 0.6m)).ToList();
        var score = VolumeAccumulationCalculator.Calculate(snapshots, new StockScoringOptions());
        Assert.True(score < 50m);
    }

    [Fact]
    public void VolumeAccumulation_NoDataAvailable_ReturnsNeutral()
    {
        var snapshots = Enumerable.Range(0, 10).Select(_ => Snapshot()).ToList(); // all null RelativeVolume
        var score = VolumeAccumulationCalculator.Calculate(snapshots, new StockScoringOptions());
        Assert.Equal(50m, score);
    }

    [Fact]
    public void VolumeAccumulation_OnlyUsesTrailingWindow()
    {
        var options = new StockScoringOptions { VolumeAccumulationWindowBars = 3 };
        var snapshots = new List<IndicatorSnapshot>
        {
            Snapshot(relativeVolume: 0.1m), Snapshot(relativeVolume: 0.1m), Snapshot(relativeVolume: 0.1m), // outside window
            Snapshot(relativeVolume: 2.0m), Snapshot(relativeVolume: 2.0m), Snapshot(relativeVolume: 2.0m), // last 3 (window)
        };
        var score = VolumeAccumulationCalculator.Calculate(snapshots, options);
        Assert.True(score > 70m, "Should reflect only the recent high-volume window, not the earlier low-volume bars.");
    }

    // --- PriceMomentumCalculator ---

    [Fact]
    public void PriceMomentum_StrongRally_ScoresHigh()
    {
        var bars = Enumerable.Range(0, 61).Select(i => Bar(i, 100m + i * 1m)).ToList(); // +60% over 60 bars
        var score = PriceMomentumCalculator.Calculate(bars, new StockScoringOptions { PriceMomentumPeriodBars = 60, PriceMomentumNormalizationScalePercent = 40m });
        Assert.True(score > 90m);
    }

    [Fact]
    public void PriceMomentum_FlatPrice_ScoresFifty()
    {
        var bars = Enumerable.Range(0, 61).Select(i => Bar(i, 100m)).ToList();
        var score = PriceMomentumCalculator.Calculate(bars, new StockScoringOptions { PriceMomentumPeriodBars = 60 });
        Assert.Equal(50m, score);
    }

    [Fact]
    public void PriceMomentum_Decline_ScoresBelowFifty()
    {
        var bars = Enumerable.Range(0, 61).Select(i => Bar(i, 100m - i * 0.3m)).ToList();
        var score = PriceMomentumCalculator.Calculate(bars, new StockScoringOptions { PriceMomentumPeriodBars = 60 });
        Assert.True(score < 50m);
    }

    [Fact]
    public void PriceMomentum_InsufficientHistory_ReturnsNeutral()
    {
        var bars = Enumerable.Range(0, 10).Select(i => Bar(i, 100m + i)).ToList();
        var score = PriceMomentumCalculator.Calculate(bars, new StockScoringOptions { PriceMomentumPeriodBars = 60 });
        Assert.Equal(50m, score);
    }

    // --- FundamentalAccelerationCalculator ---

    [Fact]
    public void FundamentalAcceleration_BothAvailable_GrowthAccelerating_ScoresAboveFifty()
    {
        var result = FundamentalAccelerationCalculator.Calculate(
            currentGrowthPercent: 25m, currentAvailability: DataAvailability.Available,
            priorGrowthPercent: 10m, priorAvailability: DataAvailability.Available,
            new StockScoringOptions());

        Assert.True(result.IsDataAvailable);
        Assert.True(result.Score > 50m);
    }

    [Fact]
    public void FundamentalAcceleration_Decelerating_ScoresBelowFifty()
    {
        var result = FundamentalAccelerationCalculator.Calculate(
            currentGrowthPercent: 5m, currentAvailability: DataAvailability.Available,
            priorGrowthPercent: 20m, priorAvailability: DataAvailability.Available,
            new StockScoringOptions());

        Assert.True(result.Score < 50m);
    }

    [Fact]
    public void FundamentalAcceleration_CurrentUnavailable_ReturnsUnavailable_NotFabricated()
    {
        var result = FundamentalAccelerationCalculator.Calculate(
            currentGrowthPercent: null, currentAvailability: DataAvailability.Unavailable,
            priorGrowthPercent: 10m, priorAvailability: DataAvailability.Available,
            new StockScoringOptions());

        Assert.False(result.IsDataAvailable);
        Assert.Equal(0m, result.Score);
    }

    [Fact]
    public void FundamentalAcceleration_PriorUnavailable_ReturnsUnavailable_EvenThoughCurrentIsKnown()
    {
        // "Acceleration" requires two points — a known current level alone must not
        // be silently scored as if it answered the acceleration question.
        var result = FundamentalAccelerationCalculator.Calculate(
            currentGrowthPercent: 30m, currentAvailability: DataAvailability.Available,
            priorGrowthPercent: null, priorAvailability: DataAvailability.Unknown,
            new StockScoringOptions());

        Assert.False(result.IsDataAvailable);
        Assert.Equal(0m, result.Score);
    }

    // --- TrendStructureCalculator ---

    [Fact]
    public void TrendStructure_AllSignalsBullish_ScoresOneHundred()
    {
        var snapshot = Snapshot(hmaFast: 105m, hmaSlow: 100m, sma50: 100m, sma200: 90m, supertrend: 98m, adx: 30m);
        var score = TrendStructureCalculator.Calculate(close: 110m, snapshot, new StrategyOptions { BreakoutMinAdx = 20m });
        Assert.Equal(100m, score);
    }

    [Fact]
    public void TrendStructure_AllSignalsBearish_ScoresZero()
    {
        // Note: ADX measures trend STRENGTH, not direction, so "all signals
        // bearish" means adx below the strength threshold (no strong trend
        // either way) here — a high ADX would correctly PASS regardless of
        // price direction, which isn't what this test is checking.
        var snapshot = Snapshot(hmaFast: 100m, hmaSlow: 105m, sma50: 100m, sma200: 110m, supertrend: 120m, adx: 10m);
        var score = TrendStructureCalculator.Calculate(close: 90m, snapshot, new StrategyOptions { BreakoutMinAdx = 20m });
        Assert.Equal(0m, score);
    }

    [Fact]
    public void TrendStructure_NoDataAvailable_EarnsNoCredit_NotANeutralFifty()
    {
        var snapshot = Snapshot(); // everything null
        var score = TrendStructureCalculator.Calculate(close: 100m, snapshot, new StrategyOptions());
        Assert.Equal(0m, score);
    }

    [Fact]
    public void TrendStructure_PartialData_RenormalizesOverAvailableSignals()
    {
        // Only HmaFast available (bullish); everything else null. Should be 100 (1/1 bullish), not penalized for missing SMA200 etc.
        var snapshot = Snapshot(hmaFast: 95m);
        var score = TrendStructureCalculator.Calculate(close: 100m, snapshot, new StrategyOptions());
        Assert.Equal(100m, score);
    }
}
