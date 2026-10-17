using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Backtesting.WalkForward;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

public class WalkForwardAggregatorTests
{
    private const decimal Capital = 1_000_000m;

    private static BacktestRunResult Test(DateOnly start, decimal[] navs, decimal finalNav, int winners, int losers, decimal windowMaxDd)
    {
        var trades = Enumerable.Range(0, winners).Select(_ => SelectionFixture.MakeTrade("AAA", 1000m))
            .Concat(Enumerable.Range(0, losers).Select(_ => SelectionFixture.MakeTrade("BBB", -500m))).ToList();
        var baseResult = SelectionFixture.MakeResult(start, start.AddDays(181), trades, cagr: 0m, maxDrawdownPercent: windowMaxDd);
        baseResult.Run.FinalCapital = finalNav;
        return new BacktestRunResult
        {
            Run = baseResult.Run, Trades = trades, TradeLegs = baseResult.TradeLegs, DrawdownEvents = baseResult.DrawdownEvents,
            DailySnapshots = navs.Select((n, i) => new PortfolioSnapshot { AsOfDate = start.AddDays(i), Nav = n }).ToList(),
        };
    }

    private static WalkForwardWindowResult Window(DateOnly testStart, BacktestRunResult test)
    {
        var w = new WalkForwardWindow(testStart.AddYears(-3), testStart.AddDays(-200), testStart.AddDays(-199), testStart.AddDays(-1), testStart, testStart.AddDays(181));
        return new WalkForwardWindowResult(w, test, test, test);
    }

    [Fact]
    public void ChainsWindowsMultiplicatively_AndMeasuresDrawdownAcrossTheBoundary()
    {
        var a = Window(new DateOnly(2024, 1, 1), Test(new DateOnly(2024, 1, 1), new[] { 1_000_000m, 1_050_000m, 990_000m, 1_100_000m }, 1_100_000m, 3, 1, 5.7m));
        var b = Window(new DateOnly(2024, 7, 1), Test(new DateOnly(2024, 7, 1), new[] { 1_000_000m, 900_000m, 950_000m }, 950_000m, 1, 2, 10m));

        var agg = WalkForwardAggregator.Aggregate(new[] { a, b }, Capital);

        Assert.Equal(2, agg.WindowsChained);
        Assert.Equal(4.5m, agg.ChainedReturnPercent);             // 1.10 * 0.95 - 1
        Assert.Equal(10m, agg.OutOfSampleMaxDrawdownPercent);     // peak 1.10 -> trough 0.99 spans the A/B boundary (A's own worst was 5.7%)
        Assert.Equal(-5m, agg.WorstWindowReturnPercent);
        Assert.Equal(10m, agg.WorstWindowDrawdownPercent);
        Assert.Equal(1, agg.ProfitableWindows);
        Assert.Equal(7, agg.TotalTrades);
        Assert.Equal(100m * 4 / 7, agg.WinRatePercent!.Value);
        Assert.InRange(agg.OutOfSampleCagrPercent!.Value, 4.4m, 4.7m); // 364 tested days ≈ one year
    }

    [Fact]
    public void OverlappingTestPeriods_AreSkippedAndReported_NotDoubleCounted()
    {
        var a = Window(new DateOnly(2024, 1, 1), Test(new DateOnly(2024, 1, 1), new[] { 1_000_000m, 1_100_000m }, 1_100_000m, 2, 0, 0m));
        var overlapping = Window(new DateOnly(2024, 3, 1), Test(new DateOnly(2024, 3, 1), new[] { 1_000_000m, 1_100_000m }, 1_100_000m, 2, 0, 0m));

        var agg = WalkForwardAggregator.Aggregate(new[] { a, overlapping }, Capital);

        Assert.Equal(1, agg.WindowsChained);
        Assert.Equal(1, agg.WindowsSkippedAsOverlapping);
        Assert.Equal(10m, agg.ChainedReturnPercent);
        Assert.Equal(2, agg.TotalTrades);
    }

    [Fact]
    public void NoWindows_GivesAllZeros_AndNoCagr()
    {
        var agg = WalkForwardAggregator.Aggregate(Array.Empty<WalkForwardWindowResult>(), Capital);

        Assert.Equal(0, agg.WindowsChained);
        Assert.Equal(0m, agg.ChainedReturnPercent);
        Assert.Null(agg.OutOfSampleCagrPercent);
        Assert.Null(agg.WinRatePercent);
    }

    [Fact]
    public void NonPositiveInitialCapital_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WalkForwardAggregator.Aggregate(Array.Empty<WalkForwardWindowResult>(), 0m));
    }

    [Fact]
    public void PooledResult_CarriesTheChainedTradesDrawdownAndRange_ForTheAntiOverfittingChecker()
    {
        var a = Window(new DateOnly(2024, 1, 1), Test(new DateOnly(2024, 1, 1), new[] { 1_000_000m, 1_100_000m }, 1_100_000m, 2, 0, 0m));
        var b = Window(new DateOnly(2024, 7, 1), Test(new DateOnly(2024, 7, 1), new[] { 1_000_000m, 500_000m }, 500_000m, 0, 3, 50m));
        var agg = WalkForwardAggregator.Aggregate(new[] { a, b }, Capital);

        var pooled = WalkForwardAggregator.ToPooledResult(new[] { a, b }, agg, Capital);

        Assert.Equal(5, pooled.Trades.Count);
        Assert.Equal(5, pooled.Run.TradeCount);
        Assert.Equal(agg.OutOfSampleMaxDrawdownPercent, pooled.Run.MaxDrawdownPercent);
        Assert.Equal(new DateOnly(2024, 1, 1), pooled.Run.StartDate);
        Assert.Equal(new DateOnly(2024, 7, 1).AddDays(181), pooled.Run.EndDate);
        Assert.True(pooled.Run.MaxDrawdownPercent > 40m, "the chained -50% window must trip the 40% acceptance ceiling");
    }
}
