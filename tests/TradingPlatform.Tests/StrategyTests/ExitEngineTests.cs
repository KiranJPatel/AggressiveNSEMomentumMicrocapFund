using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Exit;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

public class ExitEngineTests
{
    private static readonly ExitOptions Options = new() { MomentumFailureRsThreshold = 40m, DistributionVolumeMultiplier = 1.5m, SevereFundamentalDeclinePercent = -15m };

    private static Position OpenPosition(decimal currentStop = 90m) => new()
    {
        Symbol = "STK", Quantity = 100, AverageEntryPrice = 100m, InitialStop = 90m, CurrentStop = currentStop,
        InitialRiskAmount = 1000, RiskPerShare = 10m, HighestStageFilled = PyramidStage.Stage1Initial, IsOpen = true,
        OpenedAt = DateTimeOffset.UtcNow.AddDays(-10),
    };

    private static HistoricalBar Bar(decimal open, decimal high, decimal low, decimal close) => new()
    {
        Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Open = open, High = high, Low = low, Close = close,
        AdjustedClose = close, Volume = 100000, Source = "Fixture", Timeframe = Timeframe.Daily
    };

    private static IndicatorSnapshot GoodIndicators(decimal? relativeVolume = 1.0m, decimal? hmaFast = 105m, decimal? hmaSlow = 100m) => new()
    {
        Symbol = "STK", Timestamp = DateTimeOffset.UtcNow, Timeframe = Timeframe.Daily,
        RelativeVolume = relativeVolume, HmaFast = hmaFast, HmaSlow = hmaSlow
    };

    // --- Mechanism A: Stop loss ---

    [Fact]
    public void StopLoss_FiresWhenLowTouchesStop()
    {
        var position = OpenPosition(currentStop: 95m);
        var bar = Bar(open: 98m, high: 99m, low: 94m, close: 96m); // low breached stop even though close didn't

        var result = ExitEngine.Evaluate(position, bar, GoodIndicators(), 80m, RegimeClassification.Normal, null, Options);

        Assert.True(result.ShouldExit);
        Assert.Equal(ExitReason.StopLoss, result.PrimaryReason);
    }

    [Fact]
    public void StopLoss_DoesNotFire_WhenLowStaysAboveStop()
    {
        var position = OpenPosition(currentStop: 90m);
        var bar = Bar(open: 98m, high: 99m, low: 95m, close: 96m);

        var result = ExitEngine.Evaluate(position, bar, GoodIndicators(), 80m, RegimeClassification.Normal, null, Options);

        Assert.False(result.Mechanisms.Single(m => m.Reason == ExitReason.StopLoss).Fired);
    }

    // --- Mechanism B: Momentum failure (all 3 sub-conditions required) ---

    [Fact]
    public void MomentumFailure_TruePositiveCase_AllThreeHold()
    {
        var position = OpenPosition(currentStop: 50m);
        var bar = Bar(open: 100m, high: 101m, low: 89m, close: 90m); // down day, close well below fast HMA
        var indicators = GoodIndicators(relativeVolume: 2.0m, hmaFast: 95m, hmaSlow: 100m); // close(90) < fast(95) < slow(100)

        var result = ExitEngine.Evaluate(position, bar, indicators, relativeStrengthScore: 30m, RegimeClassification.Normal, null, Options);

        Assert.True(result.ShouldExit);
        Assert.Equal(ExitReason.MomentumFailure, result.PrimaryReason);
    }

    [Fact]
    public void MomentumFailure_DoesNotFire_WhenOnlyRsDeteriorated_OthersHealthy()
    {
        var position = OpenPosition(currentStop: 50m);
        var bar = Bar(open: 100m, high: 105m, low: 99m, close: 104m); // up day
        var indicators = GoodIndicators(relativeVolume: 1.0m, hmaFast: 95m, hmaSlow: 90m); // trend still fine

        var result = ExitEngine.Evaluate(position, bar, indicators, relativeStrengthScore: 20m, RegimeClassification.Normal, null, Options);

        Assert.False(result.Mechanisms.Single(m => m.Reason == ExitReason.MomentumFailure).Fired);
    }

    [Fact]
    public void MomentumFailure_DoesNotFire_WhenVolumeNotDistribution_UpDayInstead()
    {
        var position = OpenPosition(currentStop: 50m);
        var bar = Bar(open: 90m, high: 96m, low: 89m, close: 94m); // UP day (close > open), even with weak RS/HMA
        var indicators = GoodIndicators(relativeVolume: 3.0m, hmaFast: 95m, hmaSlow: 100m);

        var result = ExitEngine.Evaluate(position, bar, indicators, relativeStrengthScore: 20m, RegimeClassification.Normal, null, Options);

        Assert.False(result.Mechanisms.Single(m => m.Reason == ExitReason.MomentumFailure).Fired);
    }

    // --- Mechanism C: Fundamental failure ---

    [Fact]
    public void FundamentalFailure_NoSnapshot_DoesNotFire()
    {
        var position = OpenPosition(currentStop: 50m);
        var bar = Bar(100m, 101m, 99m, 100m);
        var result = ExitEngine.Evaluate(position, bar, GoodIndicators(), 80m, RegimeClassification.Normal, null, Options);

        Assert.False(result.Mechanisms.Single(m => m.Reason == ExitReason.FundamentalFailure).Fired);
    }

    [Fact]
    public void FundamentalFailure_SevereEarningsDecline_Fires()
    {
        var position = OpenPosition(currentStop: 50m);
        var bar = Bar(100m, 101m, 99m, 100m);
        var fundamentals = new FundamentalSnapshot
        {
            Symbol = "STK", AsOfDate = DateOnly.FromDateTime(DateTime.Today),
            EarningsGrowthPercent = -25m, EarningsGrowthAvailability = DataAvailability.Available,
            RevenueGrowthAvailability = DataAvailability.Unavailable,
        };

        var result = ExitEngine.Evaluate(position, bar, GoodIndicators(), 80m, RegimeClassification.Normal, fundamentals, Options);

        Assert.True(result.ShouldExit);
        Assert.Equal(ExitReason.FundamentalFailure, result.PrimaryReason);
    }

    [Fact]
    public void FundamentalFailure_UnavailableData_DoesNotFire_NotFabricated()
    {
        var position = OpenPosition(currentStop: 50m);
        var bar = Bar(100m, 101m, 99m, 100m);
        var fundamentals = new FundamentalSnapshot
        {
            Symbol = "STK", AsOfDate = DateOnly.FromDateTime(DateTime.Today),
            EarningsGrowthAvailability = DataAvailability.Unavailable,
            RevenueGrowthAvailability = DataAvailability.Unavailable,
        };

        var result = ExitEngine.Evaluate(position, bar, GoodIndicators(), 80m, RegimeClassification.Normal, fundamentals, Options);

        Assert.False(result.Mechanisms.Single(m => m.Reason == ExitReason.FundamentalFailure).Fired);
    }

    [Fact]
    public void FundamentalFailure_MildDecline_DoesNotFire()
    {
        var position = OpenPosition(currentStop: 50m);
        var bar = Bar(100m, 101m, 99m, 100m);
        var fundamentals = new FundamentalSnapshot
        {
            Symbol = "STK", AsOfDate = DateOnly.FromDateTime(DateTime.Today),
            EarningsGrowthPercent = -5m, EarningsGrowthAvailability = DataAvailability.Available, // mild, above -15% threshold
            RevenueGrowthAvailability = DataAvailability.Unavailable,
        };

        var result = ExitEngine.Evaluate(position, bar, GoodIndicators(), 80m, RegimeClassification.Normal, fundamentals, Options);

        Assert.False(result.Mechanisms.Single(m => m.Reason == ExitReason.FundamentalFailure).Fired);
    }

    // --- Mechanism D: Regime failure ---

    [Fact]
    public void RegimeFailure_Emergency_Fires()
    {
        var position = OpenPosition(currentStop: 50m);
        var bar = Bar(100m, 101m, 99m, 100m);
        var result = ExitEngine.Evaluate(position, bar, GoodIndicators(), 80m, RegimeClassification.Emergency, null, Options);

        Assert.True(result.ShouldExit);
        Assert.Equal(ExitReason.RegimeFailure, result.PrimaryReason);
    }

    [Fact]
    public void RegimeFailure_NonEmergency_DoesNotFire()
    {
        var position = OpenPosition(currentStop: 50m);
        var bar = Bar(100m, 101m, 99m, 100m);

        foreach (var regime in new[] { RegimeClassification.Aggressive, RegimeClassification.Normal, RegimeClassification.Cautious, RegimeClassification.Defensive })
        {
            var result = ExitEngine.Evaluate(position, bar, GoodIndicators(), 80m, regime, null, Options);
            Assert.False(result.Mechanisms.Single(m => m.Reason == ExitReason.RegimeFailure).Fired, $"Regime {regime} should not fire RegimeFailure");
        }
    }

    // --- Priority ordering and OR-gate ---

    [Fact]
    public void MultipleMechanismsFire_StopLossTakesPriority()
    {
        var position = OpenPosition(currentStop: 95m);
        var bar = Bar(open: 100m, high: 101m, low: 90m, close: 91m); // breaches stop AND is a down day
        var indicators = GoodIndicators(relativeVolume: 2.0m, hmaFast: 95m, hmaSlow: 100m); // also momentum-failure-shaped

        var result = ExitEngine.Evaluate(position, bar, indicators, relativeStrengthScore: 20m, RegimeClassification.Emergency, null, Options);

        Assert.True(result.ShouldExit);
        Assert.Equal(ExitReason.StopLoss, result.PrimaryReason); // A beats B/D even though multiple fired
        Assert.True(result.Mechanisms.Count(m => m.Fired) >= 2);
    }

    [Fact]
    public void NoMechanismFires_NoExit()
    {
        var position = OpenPosition(currentStop: 50m);
        var bar = Bar(open: 100m, high: 105m, low: 99m, close: 104m);
        var indicators = GoodIndicators(relativeVolume: 1.0m, hmaFast: 95m, hmaSlow: 90m);

        var result = ExitEngine.Evaluate(position, bar, indicators, relativeStrengthScore: 80m, RegimeClassification.Normal, null, Options);

        Assert.False(result.ShouldExit);
        Assert.True(result.PrimaryReason is null);
    }

    [Fact]
    public void AllFourMechanismsAlwaysReported_RegardlessOfOutcome()
    {
        var position = OpenPosition(currentStop: 50m);
        var bar = Bar(100m, 101m, 99m, 100m);
        var result = ExitEngine.Evaluate(position, bar, GoodIndicators(), 80m, RegimeClassification.Normal, null, Options);

        Assert.Equal(4, result.Mechanisms.Count);
        foreach (var reason in new[] { ExitReason.StopLoss, ExitReason.MomentumFailure, ExitReason.FundamentalFailure, ExitReason.RegimeFailure })
            Assert.True(result.Mechanisms.Any(m => m.Reason == reason));
    }
}
