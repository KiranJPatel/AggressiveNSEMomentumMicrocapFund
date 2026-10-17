using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.BacktestingTests;

/// <summary>
/// End-to-end proof that BacktestEngine correctly wires every prior
/// tranche's engine together into one working day-by-day simulation —
/// each individual engine already has its own unit tests; this class's
/// job is proving the SEQUENCING and DATA PLUMBING between them actually
/// works, using BacktestEngineFixture's designed entry-to-exit scenario.
/// </summary>
public class BacktestEngineTests
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

    private static BacktestRunRequest BuildRequest()
    {
        var stockBars = BacktestEngineFixture.BuildStockBars();
        var benchmarkBars = BacktestEngineFixture.BuildFlatBenchmark(stockBars.Count);

        return new BacktestRunRequest
        {
            StrategyVersion = "test-v1",
            ConfigurationHash = "test-config",
            DataVersion = "test-data",
            StartDate = DateOnly.FromDateTime(stockBars[0].Timestamp.Date),
            EndDate = DateOnly.FromDateTime(stockBars[^1].Timestamp.Date),
            SurvivorshipBiased = true,
            BroadIndexBars = benchmarkBars,
            Symbols = new[]
            {
                new BacktestSymbolContext
                {
                    Symbol = BacktestEngineFixture.Symbol,
                    Bars = stockBars,
                    MarketCapBucket = MarketCapBucket.Mid,
                },
            },
        };
    }

    [Fact]
    public void FullRun_ProducesOneCompleteTrade_EnteredAfterBreakout_StoppedOutOnTheDecline()
    {
        var result = BacktestEngine.Run(BuildRequest(), BuildOptions());

        Assert.Equal(1, result.Trades.Count);
        var trade = result.Trades[0];
        Assert.Equal(BacktestEngineFixture.Symbol, trade.Symbol);
        Assert.True(trade.EntryPrice > BacktestEngineFixture.Pivot, "Entry must fill above the pivot (NextOpen after the breakout close, spec §3.8).");
        Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
        Assert.True(trade.ExitAt > trade.EntryAt);
    }

    [Fact]
    public void FullRun_TradeIsProfitable_EntryWellBelowRunup_ExitAboveEntry()
    {
        var result = BacktestEngine.Run(BuildRequest(), BuildOptions());
        var trade = result.Trades[0];

        // The runup from ~100 to ~195 before the crash, and the crash itself only giving back
        // part of that before the stop (which trails up during the runup) catches it, should
        // net a solidly positive trade.
        Assert.True(trade.RealizedPnl > 0m, $"Expected a profitable trade; RealizedPnl was {trade.RealizedPnl}.");
        Assert.True(trade.ExitPrice > trade.EntryPrice);
    }

    [Fact]
    public void FullRun_DailySnapshots_CoverEveryTradingDate_AndAreMonotonicByDate()
    {
        var request = BuildRequest();
        var result = BacktestEngine.Run(request, BuildOptions());

        Assert.Equal(BacktestEngineFixture.BuildStockBars().Count, result.DailySnapshots.Count);
        for (var i = 1; i < result.DailySnapshots.Count; i++)
            Assert.True(result.DailySnapshots[i].AsOfDate > result.DailySnapshots[i - 1].AsOfDate);
    }

    [Fact]
    public void FullRun_NoOpenPositionsRemainAtEnd_CashFullyReconciled()
    {
        var result = BacktestEngine.Run(BuildRequest(), BuildOptions());

        var finalSnapshot = result.DailySnapshots[^1];
        // With no open positions left, NAV must equal cash exactly.
        Assert.Equal(finalSnapshot.CashBalance, finalSnapshot.Nav);
        Assert.Equal(0, finalSnapshot.OpenPositionCount);
    }

    [Fact]
    public void FullRun_RunRecord_CarriesRequestMetadata_AndSaneAggregateStats()
    {
        var request = BuildRequest();
        var result = BacktestEngine.Run(request, BuildOptions());

        Assert.Equal(request.StrategyVersion, result.Run.StrategyVersion);
        Assert.Equal(request.ConfigurationHash, result.Run.ConfigurationHash);
        Assert.True(request.SurvivorshipBiased == result.Run.SurvivorshipBiased);
        Assert.Equal(1, result.Run.TradeCount);
        Assert.NotNull(result.Run.FinalCapital);
        Assert.NotNull(result.Run.MaxDrawdownPercent);
        Assert.True(result.Run.MaxDrawdownPercent >= 0m);
    }

    [Fact]
    public void FullRun_TradeLegs_IncludeAtLeastOneStage1Leg()
    {
        var result = BacktestEngine.Run(BuildRequest(), BuildOptions());

        Assert.True(result.TradeLegs.Count >= 1);
        Assert.True(result.TradeLegs.Any(l => l.Stage == PyramidStage.Stage1Initial));
    }

    [Fact]
    public void FullRun_DrawdownEvents_TrackTierTransitions_WithoutThrowing()
    {
        // Mostly a smoke assertion: drawdown events are optional (may be empty
        // for a single mildly-profitable run) but must never contain a
        // malformed/negative percent if any were recorded.
        var result = BacktestEngine.Run(BuildRequest(), BuildOptions());

        foreach (var evt in result.DrawdownEvents)
            Assert.True(evt.DrawdownPercent >= 0m);
    }

    [Fact]
    public void EmptySymbolList_Throws()
    {
        var request = BuildRequest();
        var empty = new BacktestRunRequest
        {
            StrategyVersion = request.StrategyVersion,
            ConfigurationHash = request.ConfigurationHash,
            DataVersion = request.DataVersion,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            SurvivorshipBiased = request.SurvivorshipBiased,
            BroadIndexBars = request.BroadIndexBars,
            Symbols = Array.Empty<BacktestSymbolContext>(),
        };

        Assert.Throws<ArgumentException>(() => BacktestEngine.Run(empty, BuildOptions()));
    }

    [Fact]
    public void StartDateAfterEndDate_Throws()
    {
        var request = BuildRequest();
        var invalid = new BacktestRunRequest
        {
            StrategyVersion = request.StrategyVersion,
            ConfigurationHash = request.ConfigurationHash,
            DataVersion = request.DataVersion,
            StartDate = request.EndDate,
            EndDate = request.StartDate,
            SurvivorshipBiased = request.SurvivorshipBiased,
            BroadIndexBars = request.BroadIndexBars,
            Symbols = request.Symbols,
        };

        Assert.Throws<ArgumentException>(() => BacktestEngine.Run(invalid, BuildOptions()));
    }

    // ------------------------------------------------------------
    // Tranche 18: BoundedLookbackWindowBars (performance opt-in) —
    // see BacktestOptions.BoundedLookbackWindowBars and BacktestEngine's
    // class remarks for why this defaults to null/unbounded.
    // ------------------------------------------------------------

    [Fact]
    public void FullRun_WithGenerousBoundedWindow_ProducesIdenticalResultToUnbounded()
    {
        // This fixture's whole bar series is nowhere near 500 bars long, so
        // a 500-bar window can never actually truncate anything here —
        // BoundedSlice's "count <= maxLookback" branch takes the exact same
        // code path as the unbounded default (Take(count).ToList()) for
        // every single day of this run. A byte-for-byte identical result
        // is the expected, provable outcome, not a coincidence.
        var request = BuildRequest();
        var unboundedResult = BacktestEngine.Run(request, BuildOptions());

        var options = BuildOptions();
        var boundedResult = BacktestEngine.Run(request, new BacktestEngineOptions
        {
            Indicator = options.Indicator,
            Vcp = options.Vcp,
            Rs = options.Rs,
            StockScoring = options.StockScoring,
            Strategy = options.Strategy,
            MarketRegime = options.MarketRegime,
            Exit = options.Exit,
            Risk = options.Risk,
            Portfolio = options.Portfolio,
            Backtest = new BacktestOptions { BoundedLookbackWindowBars = 500 },
        });

        Assert.Equal(unboundedResult.Trades.Count, boundedResult.Trades.Count);
        Assert.Equal(unboundedResult.Trades[0].EntryPrice, boundedResult.Trades[0].EntryPrice);
        Assert.Equal(unboundedResult.Trades[0].ExitPrice, boundedResult.Trades[0].ExitPrice);
        Assert.Equal(unboundedResult.Trades[0].ExitReason, boundedResult.Trades[0].ExitReason);
        Assert.Equal(unboundedResult.Run.FinalCapital, boundedResult.Run.FinalCapital);
        Assert.Equal(unboundedResult.DailySnapshots.Count, boundedResult.DailySnapshots.Count);
    }

    [Fact]
    public void FullRun_WithTightBoundedWindow_CompletesWithoutThrowing_AndKeepsCoreInvariants()
    {
        // A window this tight (well under the fixture's own history and
        // under real indicator/VCP/RS lookback needs) may legitimately
        // change or eliminate the specific trade this fixture is designed
        // to produce — that's expected, not a bug, and this test
        // deliberately does NOT assert on trade outcome. Its job is proving
        // graceful degradation: the run must still complete, cover every
        // date, and never produce a nonsensical (negative/NaN) NAV.
        var request = BuildRequest();
        var options = BuildOptions();
        var tightOptions = new BacktestEngineOptions
        {
            Indicator = options.Indicator,
            Vcp = options.Vcp,
            Rs = options.Rs,
            StockScoring = options.StockScoring,
            Strategy = options.Strategy,
            MarketRegime = options.MarketRegime,
            Exit = options.Exit,
            Risk = options.Risk,
            Portfolio = options.Portfolio,
            Backtest = new BacktestOptions { BoundedLookbackWindowBars = 10 },
        };

        var result = BacktestEngine.Run(request, tightOptions);

        Assert.Equal(BacktestEngineFixture.BuildStockBars().Count, result.DailySnapshots.Count);
        foreach (var snapshot in result.DailySnapshots)
            Assert.True(snapshot.Nav >= 0m, $"NAV went negative on {snapshot.AsOfDate} with a 10-bar bounded window.");
    }

    [Fact]
    public void Run_WithAnAlreadyCancelledToken_ThrowsOperationCanceled_AndNeverReturnsAPartialResult()
    {
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
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => BacktestEngine.Run(request, options, cts.Token));
        Assert.Equal(1, BacktestEngine.Run(request, options).Trades.Count); // same inputs, no cancellation: unchanged behaviour
    }
}
