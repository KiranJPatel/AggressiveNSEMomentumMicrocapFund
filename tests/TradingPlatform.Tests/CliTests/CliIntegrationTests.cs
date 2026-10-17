using Microsoft.Extensions.Options;
using TradingPlatform.Application.Cli;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.CliTests;

/// <summary>
/// Integration tests for the CLI orchestration layer itself — as opposed to
/// the engines it wires together, which have extensive unit coverage
/// elsewhere in this project. Before this session (see docs/gap-log.md's
/// "Twenty-sixth"/"Twenty-seventh" items) the entire CLI layer, including
/// this orchestration, had never been unit-tested and had in fact never
/// even compiled. These tests call each command's real `RunAsync` against
/// a real, schema-initialized temp SQLite database and the project's real
/// config/appsettings.json (via <see cref="CliTestHost"/>) — not mocks —
/// so they exercise the actual DI-to-repository-to-engine wiring a real
/// `dotnet run -- &lt;command&gt;` invocation goes through, just without a
/// subprocess. Deliberately does not attempt `paper`/`live`/`serve` (each
/// needs either a running Kestrel host or the full live-trading state
/// machine — a larger, separate undertaking, noted as a remaining gap
/// rather than rushed) or `walkforward`/`optimize`/`montecarlo` (thin
/// wrappers around the same BacktestEngine path `backtest` already proves
/// end-to-end below).
/// </summary>
public class CliIntegrationTests
{
    private static async Task SeedGoldenVcpBreakoutAsync(CliTestHost host)
    {
        // Same proven fixture used for this session's manual seed-demo
        // verification (see docs/gap-log.md's "Twenty-sixth item") and
        // originally from VcpFixture.cs/BacktestEngineFixture.cs — reused
        // here so this integration test's expected outcome (exactly one
        // profitable-then-stopped-out trade) is backed by an
        // independently-passing unit test, not a freshly-guessed scenario.
        var now = DateTimeOffset.UtcNow;
        await host.Repo<IInstrumentRepository>().UpsertAsync(new Instrument
        {
            Symbol = "MOMENTUM1", NseSymbol = "MOMENTUM1", YahooSymbol = "MOMENTUM1.NS",
            CompanyName = "Integration Test Ltd", Sector = "Industrials", Exchange = "NSE",
            Active = true, CreatedAt = now, UpdatedAt = now,
        });

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5));
        var basePrices = new (decimal High, decimal Low, decimal Open, decimal Close, long Volume)[]
        {
            (60.0m, 58.5m, 59.25m, 59.25m, 150000L), (63.5m, 62.0m, 62.75m, 62.75m, 145000L), (67.0m, 65.5m, 66.25m, 66.25m, 155000L),
            (70.5m, 69.0m, 69.75m, 69.75m, 148000L), (74.0m, 72.5m, 73.25m, 73.25m, 152000L), (77.5m, 76.0m, 76.75m, 76.75m, 149000L),
            (81.0m, 79.5m, 80.25m, 80.25m, 151000L), (84.5m, 83.0m, 83.75m, 83.75m, 147000L), (88.0m, 86.5m, 87.25m, 87.25m, 153000L),
            (91.5m, 90.0m, 90.75m, 90.75m, 150000L), (95.0m, 93.5m, 94.25m, 94.25m, 146000L), (98.5m, 97.0m, 97.75m, 97.75m, 152000L),
            (105m, 103.5m, 104.25m, 104.25m, 220000L),
            (99.75m, 98.25m, 99.0m, 99.0m, 210000L), (94.5m, 93.0m, 93.75m, 93.75m, 190000L), (89.25m, 87.75m, 88.5m, 88.5m, 175000L),
            (84.0m, 82.5m, 83.25m, 83.25m, 160000L),
            (87.75m, 86.25m, 87.0m, 87.0m, 145000L), (91.5m, 90.0m, 90.75m, 90.75m, 140000L), (95.25m, 93.75m, 94.5m, 94.5m, 135000L),
            (99.0m, 97.5m, 98.25m, 98.25m, 130000L),
            (96.525m, 95.025m, 95.775m, 95.775m, 120000L), (94.05m, 92.55m, 93.3m, 93.3m, 110000L), (91.575m, 90.075m, 90.825m, 90.825m, 100000L),
            (89.1m, 87.6m, 88.35m, 88.35m, 95000L),
            (90.95m, 89.45m, 90.2m, 90.2m, 85000L), (92.8m, 91.3m, 92.05m, 92.05m, 80000L), (94.65m, 93.15m, 93.9m, 93.9m, 75000L),
            (96.5m, 95.0m, 95.75m, 95.75m, 70000L),
            (94.8917m, 93.3917m, 94.142m, 94.142m, 55000L), (93.2833m, 91.7833m, 92.533m, 92.533m, 45000L), (91.675m, 90.175m, 90.825m, 90.825m, 35000L),
            (92.7562m, 91.2562m, 92.006m, 92.006m, 60000L), (93.8375m, 92.3375m, 93.088m, 93.088m, 65000L), (94.9188m, 93.4188m, 94.169m, 94.169m, 70000L),
            (96.0m, 94.5m, 95.25m, 95.25m, 75000L),
        };

        var stockRows = new List<(decimal High, decimal Low, decimal Open, decimal Close, long Volume)>(basePrices);
        var close = 100m;
        stockRows.Add((101m, 98m, 99m, close, 250_000L));
        for (var i = 0; i < 44; i++)
        {
            var open = close;
            close = Math.Round(open * 1.02m, 4);
            var high = Math.Round(close * 1.01m, 4);
            var low = Math.Round(open * 0.995m, 4);
            var volume = (i + 1) % 5 == 0 ? 260_000L : 90_000L;
            stockRows.Add((high, low, open, close, volume));
        }
        for (var i = 0; i < 5; i++)
        {
            var open = Math.Round(close * 0.85m, 4);
            var low = Math.Round(open * 0.95m, 4);
            var high = Math.Round(open * 1.01m, 4);
            close = Math.Round(open * 0.98m, 4);
            stockRows.Add((high, low, open, close, 180_000L));
        }

        var stockBars = stockRows.Select((r, i) => new HistoricalBar
        {
            Symbol = "MOMENTUM1", Timestamp = start.AddDays(i), Open = r.Open, High = r.High, Low = r.Low,
            Close = r.Close, AdjustedClose = r.Close, Volume = r.Volume, Source = "integration-test", Timeframe = Timeframe.Daily,
        }).ToList();

        var benchClose = 1000m;
        var benchBars = new List<HistoricalBar>();
        // 210 sessions of index history BEFORE the stock's first bar: the regime fails closed without a 200-session trend (spec §0.4).
        for (var i = -210; i < stockRows.Count; i++)
        {
            var open = benchClose;
            benchClose = Math.Round(open * 1.0005m, 4);
            benchBars.Add(new HistoricalBar
            {
                Symbol = "^CRSLDX", Timestamp = start.AddDays(i), Open = open, High = Math.Round(benchClose * 1.002m, 4),
                Low = Math.Round(open * 0.998m, 4), Close = benchClose, AdjustedClose = benchClose, Volume = 5_000_000L,
                Source = "integration-test", Timeframe = Timeframe.Daily,
            });
        }

        await host.Repo<IHistoricalBarRepository>().UpsertRangeAsync(stockBars);
        await host.Repo<IHistoricalBarRepository>().UpsertRangeAsync(benchBars);
    }

    [Fact]
    public async Task PortfolioCommand_EmptyDatabase_ReturnsZeroAndDoesNotThrow()
    {
        using var host = CliTestHost.Create();

        var exitCode = await PortfolioCommand.RunAsync(
            host.Repo<IPortfolioSnapshotRepository>(), host.Repo<IPositionRepository>(),
            host.Repo<IDrawdownEventRepository>(), CliTestHost.Logger);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ScanCommand_NoBenchmarkDataAtAll_FailsClosedWithExitCode1()
    {
        // Correct, intentional behavior confirmed against ScanCommand's own
        // source (see the "No cached bars for the broad-index benchmark"
        // guard): with zero ^CRSLDX bars cached, scan refuses to run at all
        // rather than compute regime/relative-strength off nothing — a
        // fail-closed default consistent with this project's "never
        // fabricate data" principle throughout. A first draft of this test
        // wrongly expected exit code 0 here; this is the corrected version.
        using var host = CliTestHost.Create();

        var exitCode = await ScanCommand.RunAsync(
            host.Repo<IInstrumentRepository>(), host.Repo<IHistoricalBarRepository>(),
            host.Repo<IUniverseClassificationRepository>(), host.Repo<ISurveillanceRepository>(),
            host.Repo<IScannerResultRepository>(),
            host.Options<ScannerOptions>(), host.Options<IndicatorOptions>(), host.Options<VcpOptions>(), host.Options<RsOptions>(),
            host.Options<StockScoringOptions>(), host.Options<StrategyOptions>(), host.Options<MarketRegimeOptions>(),
            host.Options<RiskOptions>(), host.Options<PortfolioOptions>(), host.Options<BenchmarkOptions>(), CliTestHost.Logger);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ScanCommand_BenchmarkPresentButNoInstruments_ReturnsZeroWithNoResults()
    {
        using var host = CliTestHost.Create();
        await SeedGoldenVcpBreakoutAsync(host); // seeds ^CRSLDX bars (and MOMENTUM1, which scan will exclude below)

        var exitCode = await ScanCommand.RunAsync(
            host.Repo<IInstrumentRepository>(), host.Repo<IHistoricalBarRepository>(),
            host.Repo<IUniverseClassificationRepository>(), host.Repo<ISurveillanceRepository>(),
            host.Repo<IScannerResultRepository>(),
            host.Options<ScannerOptions>(), host.Options<IndicatorOptions>(), host.Options<VcpOptions>(), host.Options<RsOptions>(),
            host.Options<StockScoringOptions>(), host.Options<StrategyOptions>(), host.Options<MarketRegimeOptions>(),
            host.Options<RiskOptions>(), host.Options<PortfolioOptions>(), host.Options<BenchmarkOptions>(), CliTestHost.Logger);

        // MOMENTUM1 has only 86 bars, below ScannerOptions.MinimumHistoricalBarsRequired
        // (200) — correctly excluded by UniverseEligibilityFilter, so scan
        // still succeeds (a benchmark exists) but ranks zero candidates.
        Assert.Equal(0, exitCode);
        var latest = await host.Repo<IScannerResultRepository>().GetLatestCycleAsync();
        Assert.Empty(latest);
    }

    private static (Microsoft.Extensions.Options.IOptions<PathsOptions> Paths, string Dir) TempPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tp_cli_test_reports_{Guid.NewGuid():N}");
        return (Microsoft.Extensions.Options.Options.Create(new PathsOptions { Backtests = dir, Reports = dir, Exports = dir, Data = dir, Logs = dir }), dir);
    }

    private static Task<int> RunOptimizeAsync(CliTestHost host, string[] args, Microsoft.Extensions.Options.IOptions<PathsOptions> paths) =>
        OptimizeCommand.RunAsync(
            args, host.Repo<IInstrumentRepository>(), host.Repo<IHistoricalBarRepository>(),
            host.Repo<IUniverseClassificationRepository>(), host.Repo<ISurveillanceRepository>(),
            host.Options<IndicatorOptions>(), host.Options<VcpOptions>(), host.Options<RsOptions>(),
            host.Options<StockScoringOptions>(), host.Options<StrategyOptions>(), host.Options<MarketRegimeOptions>(),
            host.Options<ExitOptions>(), host.Options<RiskOptions>(), host.Options<PortfolioOptions>(),
            host.Options<BacktestOptions>(), host.Options<BenchmarkOptions>(), paths, CliTestHost.Logger,
            default, host.Repo<IOptimizationRunRepository>());

    [Fact]
    public async Task ScanCommand_AlreadyCancelled_StopsBeforeRankingAndWritesNothing()
    {
        using var host = CliTestHost.Create();
        await SeedGoldenVcpBreakoutAsync(host);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancelled = false;
        try
        {
            await ScanCommand.RunAsync(
                host.Repo<IInstrumentRepository>(), host.Repo<IHistoricalBarRepository>(),
                host.Repo<IUniverseClassificationRepository>(), host.Repo<ISurveillanceRepository>(),
                host.Repo<IScannerResultRepository>(),
                host.Options<ScannerOptions>(), host.Options<IndicatorOptions>(), host.Options<VcpOptions>(), host.Options<RsOptions>(),
                host.Options<StockScoringOptions>(), host.Options<StrategyOptions>(), host.Options<MarketRegimeOptions>(),
                host.Options<RiskOptions>(), host.Options<PortfolioOptions>(), host.Options<BenchmarkOptions>(), CliTestHost.Logger, cts.Token,
                regimes: host.Repo<IMarketRegimeRepository>());
        }
        catch (OperationCanceledException) { cancelled = true; }

        Assert.True(cancelled, "a cancelled token must abort the scan with OperationCanceledException (Program maps it to exit 130)");
        Assert.True(await host.Repo<IMarketRegimeRepository>().GetLatestAsync() is null, "nothing may be persisted by a cancelled scan");
        Assert.Empty(await host.Repo<IScannerResultRepository>().GetLatestCycleAsync());
    }

    [Fact]
    public async Task OptimizeCommand_SweepsRequestedParameters_AndPersistsEveryCandidateUnderOneBatch()
    {
        using var host = CliTestHost.Create();
        await SeedGoldenVcpBreakoutAsync(host);
        var (paths, dir) = TempPaths();
        try
        {
            var exit = await RunOptimizeAsync(host, new[] { "optimize", "--from", "2026-01-01", "--to", "2026-03-27", "--param", "BreakoutMinAdx=15,20,25" }, paths);

            Assert.True(exit is 0 or 2, $"exit {exit}: 0 = a candidate survived the anti-overfitting check, 2 = all were rejected; anything else is a failure");
            var rows = await host.Repo<IOptimizationRunRepository>().GetRecentAsync(10);
            Assert.Equal(3, rows.Count);
            Assert.Equal(1, rows.Select(r => r.BatchId).Distinct().Count());
            Assert.True(rows.All(r => r.ParameterSetJson.Contains("BreakoutMinAdx")));
            Assert.True(Directory.GetFiles(dir, "optimize-*.json").Length == 1, "the JSON report should have been exported");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task OptimizeCommand_BadParam_FailsWithExit1_AndWritesNothing()
    {
        using var host = CliTestHost.Create();
        var (paths, _) = TempPaths();

        var exit = await RunOptimizeAsync(host, new[] { "optimize", "--param", "NotAParameter=1,2" }, paths);

        Assert.Equal(1, exit);
        Assert.Empty(await host.Repo<IOptimizationRunRepository>().GetRecentAsync(10));
    }

    [Fact]
    public async Task OptimizeCommand_OversizedGrid_IsRefusedBeforeAnyBacktestRuns()
    {
        using var host = CliTestHost.Create();
        var (paths, _) = TempPaths();

        var exit = await RunOptimizeAsync(host, new[] { "optimize", "--param", "BreakoutMinAdx=15,20,25", "--param", "RiskPercentPerTrade=0.5,0.75,1", "--max-combinations", "5" }, paths);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task OptimizeCommand_ListParams_ExitsZero_WithoutNeedingAnyData()
    {
        using var host = CliTestHost.Create();
        var (paths, _) = TempPaths();

        Assert.Equal(0, await RunOptimizeAsync(host, new[] { "optimize", "--list-params" }, paths));
    }

    [Fact]
    public async Task BacktestCommand_CancelledMidRun_Returns130_AndLeavesNoRunStuckInRunning()
    {
        using var host = CliTestHost.Create();
        await SeedGoldenVcpBreakoutAsync(host);
        var tempDir = Path.Combine(Path.GetTempPath(), $"tp_cli_test_reports_{Guid.NewGuid():N}");
        var paths = Microsoft.Extensions.Options.Options.Create(new PathsOptions { Backtests = tempDir, Reports = tempDir, Exports = tempDir, Data = tempDir, Logs = tempDir });
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // the engine checks the token every simulated day, so it stops on the first one

        var exit = await BacktestCommand.RunAsync(
            new[] { "backtest", "--from", "2026-01-01", "--to", "2026-03-27" },
            host.Repo<IInstrumentRepository>(), host.Repo<IHistoricalBarRepository>(),
            host.Repo<IUniverseClassificationRepository>(), host.Repo<ISurveillanceRepository>(),
            host.Repo<IBacktestRunRepository>(), host.Repo<ITradeRepository>(),
            host.Options<IndicatorOptions>(), host.Options<VcpOptions>(), host.Options<RsOptions>(),
            host.Options<StockScoringOptions>(), host.Options<StrategyOptions>(), host.Options<MarketRegimeOptions>(),
            host.Options<ExitOptions>(), host.Options<RiskOptions>(), host.Options<PortfolioOptions>(),
            host.Options<BacktestOptions>(), host.Options<BenchmarkOptions>(), paths, CliTestHost.Logger, cts.Token);

        Assert.Equal(130, exit);
        var run = Assert.Single(await host.Repo<IBacktestRunRepository>().GetRunsAsync());
        Assert.Equal(BacktestRunStatus.Failed, run.Status);
        Assert.Empty(await host.Repo<IBacktestRunRepository>().GetTradesAsync(run.Id));
    }

    [Fact]
    public async Task BacktestCommand_GoldenVcpBreakout_EndToEnd_PersistsOneProfitableThenStoppedOutTrade()
    {
        using var host = CliTestHost.Create();
        await SeedGoldenVcpBreakoutAsync(host);

        var tempReportsDir = Path.Combine(Path.GetTempPath(), $"tp_cli_test_reports_{Guid.NewGuid():N}");
        var pathsOptions = Microsoft.Extensions.Options.Options.Create(new PathsOptions
        {
            Backtests = tempReportsDir, Reports = tempReportsDir, Exports = tempReportsDir, Data = tempReportsDir, Logs = tempReportsDir,
        });

        try
        {
            var args = new[] { "backtest", "--from", "2026-01-01", "--to", "2026-03-27" };
            var exitCode = await BacktestCommand.RunAsync(
                args, host.Repo<IInstrumentRepository>(), host.Repo<IHistoricalBarRepository>(),
                host.Repo<IUniverseClassificationRepository>(), host.Repo<ISurveillanceRepository>(),
                host.Repo<IBacktestRunRepository>(), host.Repo<ITradeRepository>(),
                host.Options<IndicatorOptions>(), host.Options<VcpOptions>(), host.Options<RsOptions>(),
                host.Options<StockScoringOptions>(), host.Options<StrategyOptions>(), host.Options<MarketRegimeOptions>(),
                host.Options<ExitOptions>(), host.Options<RiskOptions>(), host.Options<PortfolioOptions>(),
                host.Options<BacktestOptions>(), host.Options<BenchmarkOptions>(), pathsOptions, CliTestHost.Logger);

            Assert.Equal(0, exitCode);

            var runs = await host.Repo<IBacktestRunRepository>().GetRunsAsync();
            var run = Assert.Single(runs);
            Assert.Equal(BacktestRunStatus.Completed, run.Status);
            Assert.Equal(1, run.TradeCount);
            Assert.NotNull(run.FinalCapital);
            Assert.True(run.FinalCapital!.Value > host.Options<PortfolioOptions>().Value.InitialCapital,
                $"Expected final capital above the starting capital, got {run.FinalCapital}.");

            var trades = await host.Repo<IBacktestRunRepository>().GetTradesAsync(run.Id);
            var trade = Assert.Single(trades);
            Assert.Equal("MOMENTUM1", trade.Symbol);
            Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
            Assert.True(trade.RealizedPnl > 0m, $"Expected the trade to be net-profitable overall despite the stop-loss exit, got PnL {trade.RealizedPnl}.");
        }
        finally
        {
            try { Directory.Delete(tempReportsDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
