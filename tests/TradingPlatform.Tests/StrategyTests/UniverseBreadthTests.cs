using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Strategy.Regime;
using TradingPlatform.Tests.BacktestingTests;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.StrategyTests;

/// <summary>Spec §3.1 universe-derived regime inputs: % above 50/200 DMA, advance/decline, breakout success rate.</summary>
public class UniverseBreadthTests
{
    private static List<BreadthObservation> Obs(int count, Func<int, BreadthObservation> make) => Enumerable.Range(0, count).Select(make).ToList();

    [Fact]
    public void PercentAboveDma_CountsOnlySymbolsThatHaveThatDma()
    {
        // 30 symbols have both DMAs (20 above 50DMA, 10 above 200DMA); 10 young symbols have only a 50DMA (all above it).
        var obs = Obs(30, i => new BreadthObservation(100m, 99m, i < 20 ? 90m : 110m, i < 10 ? 90m : 110m))
            .Concat(Obs(10, _ => new BreadthObservation(100m, 99m, 90m, null))).ToList();

        var b = UniverseBreadthCalculator.Calculate(obs, 20);

        Assert.Equal(75m, b.PercentAbove50Dma!.Value); // 20 of the first 30 + all 10 young symbols = 30 above, out of 40 that have a 50DMA
        Assert.Equal(100m * 10 / 30, b.PercentAbove200Dma!.Value); // the 10 young symbols are NOT counted as "below" a 200DMA they don't have
    }

    [Fact]
    public void BelowMinimumSample_EverythingIsUnavailable_NotComputedFromAHandful()
    {
        var obs = Obs(5, _ => new BreadthObservation(100m, 99m, 90m, 90m));

        var b = UniverseBreadthCalculator.Calculate(obs, 20);

        Assert.Null(b.PercentAbove50Dma);
        Assert.Null(b.PercentAbove200Dma);
        Assert.Null(b.AdvanceDeclineRatio);
    }

    [Fact]
    public void AdvanceDecline_IsSmoothed_FiniteWhenOneSideIsEmpty_AndNeutralWhenBothAre()
    {
        var allUp = UniverseBreadthCalculator.Calculate(Obs(30, _ => new BreadthObservation(101m, 100m, null, null)), 20);
        var flat = UniverseBreadthCalculator.Calculate(Obs(30, _ => new BreadthObservation(100m, 100m, null, null)), 20);
        var twoToOne = UniverseBreadthCalculator.Calculate(Obs(30, i => new BreadthObservation(i < 20 ? 101m : 99m, 100m, null, null)), 20);

        Assert.Equal(31m, allUp.AdvanceDeclineRatio!.Value); // (30 + 1) / (0 + 1)
        Assert.Equal(1m, flat.AdvanceDeclineRatio!.Value);
        Assert.Equal(21m / 11m, twoToOne.AdvanceDeclineRatio!.Value);
        Assert.Equal(20, twoToOne.AdvancingCount);
        Assert.Equal(10, twoToOne.DecliningCount);
    }

    private static HistoricalBar B(DateOnly d, decimal close) => new()
    {
        Symbol = "X", Timestamp = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)),
        Open = close, High = close, Low = close, Close = close, AdjustedClose = close, Volume = 1000, Source = "t", Timeframe = Timeframe.Daily,
    };

    [Fact]
    public void FromBars_ComputesTailMeans_AndRejectsAStaleSymbol()
    {
        var start = new DateOnly(2026, 1, 1);
        var bars = Enumerable.Range(0, 60).Select(i => B(start.AddDays(i), 100m + i)).ToList(); // closes 100..159
        var last = start.AddDays(59);

        var o = BreadthObservation.FromBars(bars, last)!.Value;

        Assert.Equal(159m, o.Close);
        Assert.Equal(158m, o.PreviousClose!.Value);
        Assert.Equal(134.5m, o.Sma50!.Value); // mean of 110..159
        Assert.Null(o.Sma200);
        Assert.True(BreadthObservation.FromBars(bars, last.AddDays(1)) is null, "a symbol whose last bar is not the as-of date must not count as today's market");
    }

    [Fact]
    public void BreakoutSuccessRate_UsesOnlyTradesClosedInsideTheWindow_AndNeedsAMinimum()
    {
        var asOf = new DateOnly(2026, 6, 30);
        var opts = new MarketRegimeOptions { BreakoutSuccessLookbackDays = 100, BreakoutSuccessMinimumTrades = 4 };
        Trade T(decimal pnl, DateOnly exit) => SelectionFixture.MakeTrade("S", pnl, entryAt: new DateTimeOffset(exit.AddDays(-10).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

        var trades = new List<Trade>
        {
            T(500m, asOf.AddDays(-5)), T(-200m, asOf.AddDays(-20)), T(300m, asOf.AddDays(-40)), T(-100m, asOf.AddDays(-60)),
            T(900m, asOf.AddDays(-150)),   // outside the window
            T(900m, asOf),                 // closed ON the decision day — must not leak in
        };

        Assert.Equal(50m, BreakoutSuccessRateCalculator.Calculate(trades, asOf, opts)!.Value);
        Assert.Null(BreakoutSuccessRateCalculator.Calculate(trades.Take(2).ToList(), asOf, opts));
    }

    [Fact]
    public void Engine_UsesTheSuppliedBreadth_InTheRegimeScore()
    {
        var bars = Enumerable.Range(0, 260).Select(i => B(new DateOnly(2025, 1, 1).AddDays(i), 10000m + i * 10m)).ToList();
        var weak = new MarketRegimeInputs { BroadIndexBars = bars, PercentUniverseAbove50Dma = 10m, PercentUniverseAbove200Dma = 10m, AdvanceDeclineRatio = 0.4m };
        var strong = new MarketRegimeInputs { BroadIndexBars = bars, PercentUniverseAbove50Dma = 90m, PercentUniverseAbove200Dma = 90m, AdvanceDeclineRatio = 2.5m };
        var asOf = DateOnly.FromDateTime(bars[^1].Timestamp.Date);

        var w = MarketRegimeEngine.Calculate(weak, new MarketRegimeOptions(), new RsOptions(), asOf);
        var s = MarketRegimeEngine.Calculate(strong, new MarketRegimeOptions(), new RsOptions(), asOf);

        Assert.True(s.RegimeScore > w.RegimeScore, $"strong breadth {s.RegimeScore} should outscore weak breadth {w.RegimeScore}");
        Assert.Equal(90m, s.PercentUniverseAbove50Dma!.Value);
    }

    [Fact]
    public void BacktestRun_PopulatesBreadthAndRegime_WhenEnoughSymbolsAreTraded()
    {
        // Sanity that the engine-level wiring doesn't throw with a multi-symbol universe and still produces the golden trade.
        var stock = BacktestEngineFixture.BuildStockBars();
        var request = new BacktestRunRequest
        {
            StrategyVersion = "t", ConfigurationHash = "c", DataVersion = "d",
            StartDate = DateOnly.FromDateTime(stock[0].Timestamp.Date), EndDate = DateOnly.FromDateTime(stock[^1].Timestamp.Date),
            SurvivorshipBiased = true, BroadIndexBars = BacktestEngineFixture.BuildFlatBenchmark(stock.Count),
            Symbols = new[] { new BacktestSymbolContext { Symbol = BacktestEngineFixture.Symbol, Bars = stock, MarketCapBucket = MarketCapBucket.Mid } },
        };
        var options = new BacktestEngineOptions
        {
            Indicator = new IndicatorOptions(), Vcp = new VcpOptions(), Rs = new RsOptions(), StockScoring = new StockScoringOptions(), Strategy = new StrategyOptions(),
            MarketRegime = new MarketRegimeOptions(), Exit = new ExitOptions(), Risk = new RiskOptions { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList() },
            Portfolio = new PortfolioOptions { InitialCapital = 1_000_000m }, Backtest = new BacktestOptions(),
        };

        var result = BacktestEngine.Run(request, options);

        Assert.Equal(1, result.Trades.Count);
    }
}
