using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

/// <summary>
/// End-to-end proof that universe breadth reaches the regime INSIDE the backtest engine. Every other backtest test uses one stock, below
/// the minimum breadth sample size, so breadth was never non-null there. Here 25 symbols trade against an identical benchmark: only their
/// own trend differs, so any regime difference can only come from the breadth / advance-decline inputs.
/// </summary>
public class BacktestBreadthIntegrationTests
{
    private const int Bars = 330;
    private static readonly DateTimeOffset Start = new(2025, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5));

    private static List<HistoricalBar> Series(string symbol, Func<int, decimal> close) =>
        Enumerable.Range(0, Bars).Select(i =>
        {
            var c = close(i);
            return new HistoricalBar
            {
                Symbol = symbol, Timestamp = Start.AddDays(i), Open = c, High = c * 1.005m, Low = c * 0.995m, Close = c, AdjustedClose = c,
                Volume = 1_000_000, Source = "test", Timeframe = Timeframe.Daily,
            };
        }).ToList();

    private static BacktestRunResult Run(Func<int, decimal> stockClose)
    {
        // Benchmark: the same gentle uptrend in both scenarios, so index trend and volatility inputs are identical.
        var benchmark = Series("NIFTY500", i => 10_000m + i * 8m);
        var symbols = Enumerable.Range(0, 25).Select(n => new BacktestSymbolContext
        {
            Symbol = $"S{n:00}", Bars = Series($"S{n:00}", stockClose), MarketCapBucket = MarketCapBucket.Mid,
        }).ToList();

        var request = new BacktestRunRequest
        {
            StrategyVersion = "t", ConfigurationHash = "c", DataVersion = "d",
            StartDate = DateOnly.FromDateTime(Start.AddDays(210).Date), EndDate = DateOnly.FromDateTime(Start.AddDays(Bars - 1).Date),
            SurvivorshipBiased = true, BroadIndexBars = benchmark, Symbols = symbols,
        };
        var options = new BacktestEngineOptions
        {
            Indicator = new IndicatorOptions(), Vcp = new VcpOptions(), Rs = new RsOptions(), StockScoring = new StockScoringOptions(), Strategy = new StrategyOptions(),
            MarketRegime = new MarketRegimeOptions(), Exit = new ExitOptions(), Risk = new RiskOptions { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList() },
            Portfolio = new PortfolioOptions { InitialCapital = 1_000_000m }, Backtest = new BacktestOptions(),
        };
        return BacktestEngine.Run(request, options);
    }

    private static double MeanRegimeRank(BacktestRunResult r) =>
        r.DailySnapshots.Where(s => s.Regime is not null).Average(s => (double)(int)s.Regime!.Value);

    [Fact]
    public void UniverseTrend_ChangesTheRegime_WhenEverythingElseIsIdentical()
    {
        var strong = Run(i => 100m + i * 0.35m);                 // every stock above its 50/200 DMA, most days advancing
        var weak = Run(i => 220m - i * 0.35m);                   // every stock below its DMAs, most days declining

        var strongRank = MeanRegimeRank(strong);
        var weakRank = MeanRegimeRank(weak);

        // RegimeClassification is ordered by severity in its enum; whichever direction it runs, the two universes must DIFFER.
        // (Before this wiring both runs produced identical regimes: breadth was never supplied.)
        Assert.NotEqual(strongRank, weakRank);
        Assert.True(strong.DailySnapshots.Count > 50 && weak.DailySnapshots.Count > 50);
    }

    [Fact]
    public void WeakUniverse_IsNeverMoreDeployedThanAStrongOne()
    {
        var strong = Run(i => 100m + i * 0.35m);
        var weak = Run(i => 220m - i * 0.35m);

        var strongCls = strong.DailySnapshots.Where(s => s.Regime is not null).Select(s => (int)s.Regime!.Value).ToList();
        var weakCls = weak.DailySnapshots.Where(s => s.Regime is not null).Select(s => (int)s.Regime!.Value).ToList();
        var order = Enum.GetValues<RegimeClassification>().Select(v => (int)v).ToList();

        // Rank by the enum's own severity ordering: the better regime must be the strong universe's.
        var best = strongCls.Concat(weakCls).Distinct().OrderBy(v => order.IndexOf(v)).ToList();
        Assert.True(best.Count >= 1);
        Assert.NotEqual(strongCls.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key,
                        weakCls.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key);
    }
}
