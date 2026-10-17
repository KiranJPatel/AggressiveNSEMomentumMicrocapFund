using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Scanner.Universe;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ScannerTests;

public class LiquidityMetricsCalculatorTests
{
    private static HistoricalBar Bar(decimal close, long volume, int daysAgo) => new()
    {
        Symbol = "ABC", Timestamp = DateTimeOffset.UtcNow.AddDays(-daysAgo), Open = close, High = close, Low = close, Close = close,
        AdjustedClose = close, Volume = volume, Source = "Fixture", Timeframe = Timeframe.Daily,
    };

    [Fact]
    public void Calculate_UsesOnlyTheLookbackWindow_NotOlderBars()
    {
        var bars = new List<HistoricalBar>();
        for (var i = 30; i >= 21; i--) bars.Add(Bar(100m, 1_000_000, i)); // older, outside a 20-day lookback
        for (var i = 20; i >= 1; i--) bars.Add(Bar(100m, 10_000, i));    // recent 20 days

        var metrics = LiquidityMetricsCalculator.Calculate(bars, lookbackDays: 20);

        Assert.Equal(10_000m, metrics.AverageDailyVolume); // only the recent, lower-volume days counted
    }

    [Fact]
    public void Calculate_ComputesAverageTradedValue()
    {
        var bars = new List<HistoricalBar> { Bar(100m, 1000, 1), Bar(200m, 1000, 2) }; // traded values: 100,000 and 200,000
        var metrics = LiquidityMetricsCalculator.Calculate(bars, lookbackDays: 20);
        Assert.Equal(150_000m, metrics.AverageDailyTradedValue);
    }

    [Fact]
    public void Calculate_MedianTradedValue_OddCount()
    {
        var bars = new List<HistoricalBar> { Bar(100m, 1000, 1), Bar(300m, 1000, 2), Bar(200m, 1000, 3) }; // values: 100k, 300k, 200k
        var metrics = LiquidityMetricsCalculator.Calculate(bars, lookbackDays: 20);
        Assert.Equal(200_000m, metrics.MedianDailyTradedValue);
    }

    [Fact]
    public void Calculate_EmptyBars_ReturnsZeros()
    {
        var metrics = LiquidityMetricsCalculator.Calculate(Array.Empty<HistoricalBar>(), lookbackDays: 20);
        Assert.Equal(0m, metrics.AverageDailyVolume);
        Assert.Equal(0m, metrics.AverageDailyTradedValue);
    }
}
