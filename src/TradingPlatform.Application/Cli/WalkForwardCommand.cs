using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Backtesting.WalkForward;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// `dotnet run -- walkforward [--from --to] [--train-years N]
/// [--validate-months N] [--test-months N] [--step-months N]` (spec §5.5,
/// §7.3) — rolling Train/Validate/Test windows (default 3y/6m/6m, spec's
/// own example) via WalkForwardEngine (Tranche 13), scored by
/// CompositeScoreCalculator.CalculateWalkForwardStability. Only each
/// window's out-of-sample TEST result is persisted to BacktestRuns (Train
/// and Validate exist purely to produce that Test result under
/// walk-forward's no-look-ahead discipline and aren't independently
/// interesting artifacts) — a resolved scope choice, not an oversight.
/// </summary>
public static class WalkForwardCommand
{
    private const string StrategyVersionTag = "AggressiveMomentumV1-Tranche19-WalkForward";

    public static async Task<int> RunAsync(
        string[] args, IInstrumentRepository instruments, IHistoricalBarRepository bars,
        IUniverseClassificationRepository classifications, ISurveillanceRepository surveillance,
        IBacktestRunRepository runRepo, ITradeRepository tradeRepo,
        IOptions<IndicatorOptions> indicatorOptions, IOptions<VcpOptions> vcpOptions, IOptions<RsOptions> rsOptions,
        IOptions<StockScoringOptions> stockScoringOptions, IOptions<StrategyOptions> strategyOptions,
        IOptions<MarketRegimeOptions> marketRegimeOptions, IOptions<ExitOptions> exitOptions, IOptions<RiskOptions> riskOptions,
        IOptions<PortfolioOptions> portfolioOptions, IOptions<BacktestOptions> backtestOptions, IOptions<BenchmarkOptions> benchmarkOptions,
        ILogger logger, CancellationToken cancellationToken = default, IDeliveryDataRepository? deliveryRepo = null)
    {
        var to = ParseDateArg(args, "--to", out var toInvalid) ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        if (toInvalid) { Console.Error.WriteLine("--to value is not a valid date (expected yyyy-MM-dd)."); return 1; }
        var from = ParseDateArg(args, "--from", out var fromInvalid) ?? to.AddYears(-5);
        if (fromInvalid) { Console.Error.WriteLine("--from value is not a valid date (expected yyyy-MM-dd)."); return 1; }
        var trainYears = (int)(ParseLongArg(args, "--train-years", out var trainInvalid) ?? 3);
        if (trainInvalid) { Console.Error.WriteLine("--train-years value must be a valid integer."); return 1; }
        var validateMonths = (int)(ParseLongArg(args, "--validate-months", out var validateInvalid) ?? 6);
        if (validateInvalid) { Console.Error.WriteLine("--validate-months value must be a valid integer."); return 1; }
        var testMonths = (int)(ParseLongArg(args, "--test-months", out var testInvalid) ?? 6);
        if (testInvalid) { Console.Error.WriteLine("--test-months value must be a valid integer."); return 1; }
        var stepMonthsRaw = ParseLongArg(args, "--step-months", out var stepInvalid);
        if (stepInvalid) { Console.Error.WriteLine("--step-months value must be a valid integer."); return 1; }
        var stepMonths = stepMonthsRaw is { } s ? (int)s : (int?)null;

        IReadOnlyList<WalkForwardWindow> windows;
        try
        {
            windows = WalkForwardWindowGenerator.GenerateWindows(from, to, trainYears, validateMonths, testMonths, stepMonths);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Invalid walk-forward window parameters: {ex.Message}");
            return 1;
        }

        if (windows.Count == 0)
        {
            Console.Error.WriteLine($"No complete {trainYears}y/{validateMonths}m/{testMonths}m window fits in {from:yyyy-MM-dd}..{to:yyyy-MM-dd} — widen the range or shorten the window.");
            return 1;
        }

        var context = await BacktestDataContext.LoadAsync(instruments, bars, classifications, surveillance, benchmarkOptions.Value, from, to, logger, cancellationToken, refinementMode: backtestOptions.Value.SignalToOrderLag,
            deliveries: deliveryRepo, scoring: stockScoringOptions.Value, strategy: strategyOptions.Value);
        if (context is null)
        {
            Console.Error.WriteLine("No cached bars available for the requested range — run 'dotnet run -- fetch' first.");
            return 1;
        }

        var engineOptions = new BacktestEngineOptions
        {
            Indicator = indicatorOptions.Value, Vcp = vcpOptions.Value, Rs = rsOptions.Value, StockScoring = stockScoringOptions.Value,
            Strategy = strategyOptions.Value, MarketRegime = marketRegimeOptions.Value, Exit = exitOptions.Value, Risk = riskOptions.Value,
            Portfolio = portfolioOptions.Value, Backtest = backtestOptions.Value,
        };
        var configurationHash = ComputeConfigurationHash(engineOptions);

        BacktestRunResult RunSubPeriod(DateOnly start, DateOnly end)
        {
                        return BacktestEngine.Run(context.BuildRequest(start, end, StrategyVersionTag, configurationHash), engineOptions, cancellationToken);
        }

        var windowResults = WalkForwardEngine.Run(windows, RunSubPeriod);
        var stability = CompositeScoreCalculator.CalculateWalkForwardStability(windowResults);

        Console.WriteLine("========================================================");
        Console.WriteLine($" WALK-FORWARD — {windows.Count} window(s), {trainYears}y/{validateMonths}m/{testMonths}m");
        Console.WriteLine("========================================================");
        Console.WriteLine($"{"#",-3}{"Test Period",-24}{"Train CAGR",-12}{"Test CAGR",-11}{"Test Sharpe",-12}{"Test MaxDD",-11}Test Trades");

        for (var i = 0; i < windowResults.Count; i++)
        {
            var w = windowResults[i];
            var runId = await PersistTestResultAsync(runRepo, tradeRepo, w, configurationHash, context.DataVersion, cancellationToken);
            Console.WriteLine(
                $"{i + 1,-3}{w.Window.TestStart:yyyy-MM-dd} - {w.Window.TestEnd:yyyy-MM-dd}  {Pct(w.TrainResult.Run.Cagr),-12}{Pct(w.TestResult.Run.Cagr),-11}{Num(w.TestResult.Run.Sharpe),-12}{Pct(w.TestResult.Run.MaxDrawdownPercent),-11}{w.TestResult.Run.TradeCount ?? 0}  (run {runId})");
        }

        Console.WriteLine();
        Console.WriteLine($"Walk-forward stability score: {stability:F2} (higher = more consistent out-of-sample; penalizes both variance across windows and out-of-sample degradation vs Train — see CompositeScoreCalculator)");

        // Aggregate out-of-sample picture (spec §8.2) + the §5.7 anti-overfitting verdict over the chained Test periods.
        var initialCapital = portfolioOptions.Value.InitialCapital;
        var aggregate = WalkForwardAggregator.Aggregate(windowResults, initialCapital);
        var pooled = WalkForwardAggregator.ToPooledResult(windowResults, aggregate, initialCapital);
        var verdict = AntiOverfittingChecker.Evaluate(
            pooled, windowResults, pooled.Trades.GroupBy(t => t.Symbol).ToDictionary(g => g.Key, g => g.Count()),
            backtestOptions.Value.MaxAcceptableDrawdownPercent, 30, marketRegimeOptions.Value.AggressiveThreshold);
        PrintAggregate(aggregate, verdict);
        Console.WriteLine("========================================================");

        logger.LogInformation("walkforward: {WindowCount} windows ({Chained} chained), stability {Stability}, OOS CAGR {Cagr}, OOS maxDD {Dd}, rejected={Rejected}.",
            windows.Count, aggregate.WindowsChained, stability, aggregate.OutOfSampleCagrPercent, aggregate.OutOfSampleMaxDrawdownPercent, verdict.Rejected);
        return verdict.Rejected ? 2 : 0; // 2 = ran fine but the strategy fails spec §5.7 acceptance (scriptable, distinct from 1 = bad input/no data)
    }

    private static async Task<long> PersistTestResultAsync(
        IBacktestRunRepository runRepo, ITradeRepository tradeRepo, WalkForwardWindowResult window,
        string configurationHash, string dataVersion, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var pending = new BacktestRun
        {
            StrategyVersion = StrategyVersionTag,
            ConfigurationHash = configurationHash,
            DataVersion = dataVersion,
            StartDate = window.Window.TestStart,
            EndDate = window.Window.TestEnd,
            Status = BacktestRunStatus.Pending,
            SurvivorshipBiased = true,
            StartedAt = startedAt,
        };
        var runId = await runRepo.InsertStartedAsync(pending, cancellationToken);

        var result = window.TestResult;
        var dbTradeIds = new List<long>(result.Trades.Count);
        foreach (var trade in result.Trades)
        {
            var legs = result.TradeLegs.Where(l => l.TradeId == trade.Id).OrderBy(l => l.FilledAt).ToList();
            dbTradeIds.Add(await tradeRepo.InsertAsync(trade, legs, cancellationToken));
        }
        if (dbTradeIds.Count > 0) await runRepo.LinkTradesAsync(runId, dbTradeIds, cancellationToken);
        await runRepo.InsertSnapshotsAsync(runId, result.DailySnapshots, cancellationToken);
        if (result.DrawdownEvents.Count > 0) await runRepo.InsertDrawdownEventsAsync(runId, result.DrawdownEvents, cancellationToken);

        result.Run.Id = runId;
        await runRepo.UpdateCompletedAsync(result.Run, cancellationToken);
        return runId;
    }

    internal static void PrintAggregate(WalkForwardAggregate a, AntiOverfittingReport verdict)
    {
        Console.WriteLine();
        Console.WriteLine($"Out-of-sample (Test periods chained, {a.WindowsChained} of {a.WindowsTotal} window(s){(a.WindowsSkippedAsOverlapping > 0 ? $"; {a.WindowsSkippedAsOverlapping} overlapping skipped — use --step-months >= --test-months for full coverage" : "")}):");
        Console.WriteLine($"  Return {a.ChainedReturnPercent:F1}%   CAGR {Pct(a.OutOfSampleCagrPercent)}   Max drawdown {a.OutOfSampleMaxDrawdownPercent:F1}%   Trades {a.TotalTrades}   Win rate {Pct(a.WinRatePercent)}");
        Console.WriteLine($"  Profitable windows {a.ProfitableWindows}/{a.WindowsChained}   Worst window return {a.WorstWindowReturnPercent:F1}%   Worst window drawdown {a.WorstWindowDrawdownPercent:F1}%   Days tested {a.TestedCalendarDays}");
        Console.WriteLine(verdict.Rejected ? "  VERDICT: REJECTED by the anti-overfitting check (spec §5.7) — do not trade this configuration." : "  VERDICT: not rejected (warnings below are for a human to weigh).");
        foreach (var f in verdict.Flags.Where(f => f.Triggered)) Console.WriteLine($"    {(f.Name is "ExcessiveDrawdown" or "OutOfSamplePerformanceDisappears" ? "REJECT" : "warn  ")} {f.Name}: {f.Detail}");
        Console.WriteLine("  The configuration under test was NOT re-optimized per window; these are fixed-parameter out-of-sample results.");
    }

    private static string Pct(decimal? v) => v is null ? "n/a" : $"{v.Value:F1}%";
    private static string Num(decimal? v) => v is null ? "n/a" : $"{v.Value:F2}";

    private static string ComputeConfigurationHash(BacktestEngineOptions options)
    {
        var json = JsonSerializer.Serialize(options);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
    }

    internal static DateOnly? ParseDateArg(string[] args, string flag, out bool invalid)
    {
        invalid = false;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] != flag) continue;
            if (!DateOnly.TryParse(args[i + 1], out var v)) { invalid = true; return null; }
            return v;
        }
        return null;
    }

    internal static long? ParseLongArg(string[] args, string flag, out bool invalid)
    {
        invalid = false;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] != flag) continue;
            if (!long.TryParse(args[i + 1], out var v)) { invalid = true; return null; }
            return v;
        }
        return null;
    }
}
