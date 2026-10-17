using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Risk.Analytics;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.RiskTests;

public class TradeAnalyticsCalculatorTests
{
    private static Trade MakeTrade(decimal rMultiple, decimal realizedPnl) => new()
    {
        Symbol = "STK", EntryPrice = 100m, InitialStop = 90m, InitialRiskAmount = 1000m,
        ExitPrice = 100m + rMultiple * 10m, ExitReason = ExitReason.StopLoss,
        RealizedPnl = realizedPnl, RMultiple = rMultiple,
        MaximumFavorableExcursion = Math.Max(rMultiple, 0), MaximumAdverseExcursion = Math.Min(rMultiple, 0),
        RegimeScoreAtEntry = 70, StockScoreAtEntry = 75m, RiskAtEntryPercent = 1m,
        StrategyVersion = "v1", ConfigurationVersion = "v1",
        EntryAt = DateTimeOffset.UtcNow.AddDays(-10), ExitAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void EmptyTradeList_ReturnsAllNulls_ZeroCount()
    {
        var summary = TradeAnalyticsCalculator.Calculate(Array.Empty<Trade>());

        Assert.Equal(0, summary.TradeCount);
        Assert.True(summary.AverageR is null);
        Assert.True(summary.WinRatePercent is null);
        Assert.True(summary.ProfitFactor is null);
    }

    [Fact]
    public void AverageR_MatchesManualMean()
    {
        var trades = new[] { MakeTrade(2m, 2000m), MakeTrade(-1m, -1000m), MakeTrade(3m, 3000m) };
        var summary = TradeAnalyticsCalculator.Calculate(trades);

        AssertNear((2m + -1m + 3m) / 3m, summary.AverageR!.Value, 0.0001m);
    }

    [Fact]
    public void MedianR_OddCount_IsMiddleValue()
    {
        var trades = new[] { MakeTrade(1m, 100m), MakeTrade(5m, 500m), MakeTrade(3m, 300m) };
        var summary = TradeAnalyticsCalculator.Calculate(trades);
        Assert.Equal(3m, summary.MedianR);
    }

    [Fact]
    public void MedianR_EvenCount_IsAverageOfMiddleTwo()
    {
        var trades = new[] { MakeTrade(1m, 100m), MakeTrade(2m, 200m), MakeTrade(3m, 300m), MakeTrade(4m, 400m) };
        var summary = TradeAnalyticsCalculator.Calculate(trades);
        Assert.Equal(2.5m, summary.MedianR);
    }

    [Fact]
    public void WinRate_MatchesManualCalculation()
    {
        var trades = new[] { MakeTrade(2m, 2000m), MakeTrade(-1m, -1000m), MakeTrade(1m, 1000m), MakeTrade(-0.5m, -500m) };
        var summary = TradeAnalyticsCalculator.Calculate(trades);
        Assert.Equal(50m, summary.WinRatePercent); // 2 of 4 winners
    }

    [Fact]
    public void AverageWinnerAndLoser_AreExpressedInRMultiples()
    {
        var trades = new[] { MakeTrade(2m, 2000m), MakeTrade(4m, 4000m), MakeTrade(-1m, -1000m), MakeTrade(-2m, -2000m) };
        var summary = TradeAnalyticsCalculator.Calculate(trades);

        AssertNear(3m, summary.AverageWinnerR!.Value, 0.0001m);  // (2+4)/2
        AssertNear(-1.5m, summary.AverageLoserR!.Value, 0.0001m); // (-1-2)/2
    }

    [Fact]
    public void ProfitFactor_MatchesGrossProfitOverGrossLoss()
    {
        var trades = new[] { MakeTrade(2m, 2000m), MakeTrade(1m, 1000m), MakeTrade(-1m, -1500m) };
        var summary = TradeAnalyticsCalculator.Calculate(trades);

        // Gross profit = 3000, gross loss = 1500 -> PF = 2.0
        AssertNear(2.0m, summary.ProfitFactor!.Value, 0.0001m);
    }

    [Fact]
    public void ProfitFactor_NoLosers_ReturnsNull_NotInfinity()
    {
        var trades = new[] { MakeTrade(2m, 2000m), MakeTrade(1m, 1000m) };
        var summary = TradeAnalyticsCalculator.Calculate(trades);
        Assert.True(summary.ProfitFactor is null);
    }

    [Fact]
    public void NoWinners_AverageWinnerRIsNull_NotZero()
    {
        var trades = new[] { MakeTrade(-1m, -1000m), MakeTrade(-2m, -2000m) };
        var summary = TradeAnalyticsCalculator.Calculate(trades);
        Assert.True(summary.AverageWinnerR is null);
    }

    [Fact]
    public void Expectancy_MatchesManualCalculation()
    {
        // 2 winners averaging +3R, 2 losers averaging -1.5R -> expectancy = 0.5*3 + 0.5*(-1.5) = 0.75R
        var trades = new[] { MakeTrade(2m, 2000m), MakeTrade(4m, 4000m), MakeTrade(-1m, -1000m), MakeTrade(-2m, -2000m) };
        var summary = TradeAnalyticsCalculator.Calculate(trades);

        AssertNear(0.75m, summary.ExpectancyR!.Value, 0.0001m);
    }

    [Fact]
    public void BreakEvenTrade_ZeroPnl_CountsAsLoserForWinRate()
    {
        // RealizedPnl == 0 is not a "winner" (spec's implicit convention: winner = positive PnL, not >= 0).
        var trades = new[] { MakeTrade(0m, 0m), MakeTrade(2m, 2000m) };
        var summary = TradeAnalyticsCalculator.Calculate(trades);
        Assert.Equal(50m, summary.WinRatePercent);
    }

    private static void AssertNear(decimal expected, decimal actual, decimal tolerance)
    {
        var diff = Math.Abs(expected - actual);
        Assert.True(diff <= tolerance, $"expected {expected}, got {actual} (diff {diff})");
    }
}
