using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Backtesting.WalkForward;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

/// <summary>
/// Proves WalkForwardEngine's delegate contract genuinely wires up a real
/// BacktestEngine.Run call — not just the fake-delegate unit tests in
/// WalkForwardEngineTests. Deliberately uses artificially short Train/
/// Validate/Test periods (weeks, not the spec's typical 3y/6m/6m) that
/// fit within BacktestEngineFixture's existing ~86-day scenario (Tranche
/// 11) — a multi-year fixture large enough for spec-realistic window
/// sizes would be impractical to hand-engineer, and isn't what this test
/// needs to prove: the wiring between WalkForwardEngine and
/// BacktestEngine.Run is exactly as valid at any window size.
/// </summary>
public class WalkForwardIntegrationTests
{
    private static BacktestEngineOptions BuildOptions() => new()
    {
        Indicator = new IndicatorOptions(),
        Vcp = new VcpOptions(),
        Rs = new RsOptions(),
        StockScoring = new StockScoringOptions(),
        Strategy = new StrategyOptions(),
        MarketRegime = new MarketRegimeOptions(),
        Exit = new ExitOptions(),
        Risk = new RiskOptions { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList() },
        Portfolio = new PortfolioOptions { InitialCapital = 1_000_000m },
        Backtest = new BacktestOptions(),
    };

    [Fact]
    public void WalkForwardEngine_DrivingRealBacktestEngine_ProducesConsistentSubPeriodResults()
    {
        var stockBars = BacktestEngineFixture.BuildStockBars();
        var benchmarkBars = BacktestEngineFixture.BuildFlatBenchmark(stockBars.Count);
        var options = BuildOptions();

        var symbols = new[]
        {
            new BacktestSymbolContext { Symbol = BacktestEngineFixture.Symbol, Bars = stockBars, MarketCapBucket = MarketCapBucket.Mid },
        };

        BacktestRunResult RunBacktest(DateOnly start, DateOnly end) => BacktestEngine.Run(new BacktestRunRequest
        {
            StrategyVersion = "wf-test", ConfigurationHash = "c1", DataVersion = "d1",
            StartDate = start, EndDate = end, SurvivorshipBiased = true,
            BroadIndexBars = benchmarkBars, Symbols = symbols,
        }, options);

        // A single short window entirely within the fixture's ~86-day span.
        var window = new WalkForwardWindow(
            TrainStart: DateOnly.FromDateTime(stockBars[0].Timestamp.Date),
            TrainEnd: DateOnly.FromDateTime(stockBars[40].Timestamp.Date),
            ValidateStart: DateOnly.FromDateTime(stockBars[41].Timestamp.Date),
            ValidateEnd: DateOnly.FromDateTime(stockBars[60].Timestamp.Date),
            TestStart: DateOnly.FromDateTime(stockBars[61].Timestamp.Date),
            TestEnd: DateOnly.FromDateTime(stockBars[^1].Timestamp.Date));

        var results = WalkForwardEngine.Run(new[] { window }, RunBacktest);

        Assert.Equal(1, results.Count);
        var result = results[0];

        // Each sub-period's daily snapshot count should match its own date range, not leak
        // into a neighboring sub-period's days.
        Assert.True(result.TrainResult.DailySnapshots.Count > 0);
        Assert.True(result.ValidateResult.DailySnapshots.Count > 0);
        Assert.True(result.TestResult.DailySnapshots.Count > 0);
        Assert.Equal(window.TrainStart, result.TrainResult.Run.StartDate);
        Assert.Equal(window.TestEnd, result.TestResult.Run.EndDate);

        // The composite score calculator should be able to consume these real results without throwing.
        var score = CompositeScoreCalculator.CalculateWalkForwardStability(results);
        Assert.True(score > decimal.MinValue); // just proving it computed a real number, not a crash
    }
}
