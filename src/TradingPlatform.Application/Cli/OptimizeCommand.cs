using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Reporting.Reports;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// `dotnet run -- optimize [--from --to] [--param Name=v1,v2,...]... [--list-params] [--max-combinations N] [--top N]` (spec §5.5, §7.3).
///
/// A grid search over any of the spec-§5.5 parameters in <see cref="OptimizerParameterCatalog"/> (`--list-params` prints them with their
/// default value ladders). With no `--param` the original two-parameter grid (RiskPercentPerTrade x StopAtrMultiplierMin) runs. Each
/// combination is backtested on a Train slice (first 70% of the range) AND a Validate slice (last 30%); ranking uses the Validate score
/// minus any Train-over-Validate degradation, averaged with the scores of adjacent grid cells so a lone spike cannot beat a plateau
/// (<see cref="OptimizationRanker"/>), and a candidate that fails the §5.7 anti-overfitting check (drawdown above the ceiling, or Train
/// positive but Validate not) can never win. Every candidate is stored in the OptimizationRuns table under one BatchId.
///
/// The Validate slice is still the data the winner is chosen on, so it is not a clean out-of-sample test: run `walkforward` with the
/// winning values applied in config before trusting the result.
/// </summary>
public static class OptimizeCommand
{
    private const string StrategyVersionTag = "AggressiveMomentumV1-Optimize";
    private const int DefaultMaxCombinations = 200;
    private const int DefaultTop = 15;
    private static readonly string[] DefaultParameterNames = { "RiskPercentPerTrade", "StopAtrMultiplierMin" };

    public static async Task<int> RunAsync(
        string[] args, IInstrumentRepository instruments, IHistoricalBarRepository bars,
        IUniverseClassificationRepository classifications, ISurveillanceRepository surveillance,
        IOptions<IndicatorOptions> indicatorOptions, IOptions<VcpOptions> vcpOptions, IOptions<RsOptions> rsOptions,
        IOptions<StockScoringOptions> stockScoringOptions, IOptions<StrategyOptions> strategyOptions,
        IOptions<MarketRegimeOptions> marketRegimeOptions, IOptions<ExitOptions> exitOptions, IOptions<RiskOptions> riskOptions,
        IOptions<PortfolioOptions> portfolioOptions, IOptions<BacktestOptions> backtestOptions, IOptions<BenchmarkOptions> benchmarkOptions,
        IOptions<PathsOptions> pathsOptions, ILogger logger, CancellationToken cancellationToken = default,
        IOptimizationRunRepository? runRepository = null, IDeliveryDataRepository? deliveryRepo = null)
    {
        if (args.Contains("--list-params")) { PrintCatalog(); return 0; }

        var parsed = OptimizeArguments.Parse(args);
        if (parsed.Error is not null) { Console.Error.WriteLine(parsed.Error); return 1; }

        var to = ParseDateArg(args, "--to", out var toInvalid) ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        if (toInvalid) { Console.Error.WriteLine("--to value is not a valid date (expected yyyy-MM-dd)."); return 1; }
        var from = ParseDateArg(args, "--from", out var fromInvalid) ?? to.AddYears(-5);
        if (fromInvalid) { Console.Error.WriteLine("--from value is not a valid date (expected yyyy-MM-dd)."); return 1; }
        if (from >= to)
        {
            Console.Error.WriteLine($"--from ({from:yyyy-MM-dd}) must be before --to ({to:yyyy-MM-dd}).");
            return 1;
        }

        // 70/30 Train/Validate split of the requested range by day count.
        var totalDays = to.DayNumber - from.DayNumber;
        var trainEnd = from.AddDays((int)(totalDays * 0.7));
        var validateStart = trainEnd.AddDays(1);
        if (validateStart >= to)
        {
            Console.Error.WriteLine("Requested range is too short to split into a meaningful Train/Validate pair — widen --from/--to.");
            return 1;
        }

        var baseline = new BacktestEngineOptions
        {
            Indicator = indicatorOptions.Value, Vcp = vcpOptions.Value, Rs = rsOptions.Value, StockScoring = stockScoringOptions.Value,
            Strategy = strategyOptions.Value, MarketRegime = marketRegimeOptions.Value, Exit = exitOptions.Value, Risk = riskOptions.Value,
            Portfolio = portfolioOptions.Value, Backtest = backtestOptions.Value,
        };

        // Resolve the swept dimensions (explicit --param, else the default two) and expand the grid, skipping invariant-breaking combinations.
        var requested = parsed.Parameters.Count > 0
            ? parsed.Parameters
            : DefaultParameterNames.Select(n => new OptimizeArguments.Sweep(n, OptimizerParameterCatalog.Find(n)!.DefaultValues.OrderBy(v => v).ToList())).ToList();
        var gridParameters = requested.Select(r =>
        {
            var def = OptimizerParameterCatalog.Find(r.Name)!;
            return new GridParameter<BacktestEngineOptions>(def.Name, r.Values, def.Apply);
        }).ToList();

        var rawCombinations = gridParameters.Aggregate(1L, (acc, p) => acc * p.Values.Count);
        if (rawCombinations > parsed.MaxCombinations)
        {
            Console.Error.WriteLine($"The grid has {rawCombinations} combinations, above --max-combinations {parsed.MaxCombinations} (each costs two backtests). Narrow the value lists or raise --max-combinations.");
            return 1;
        }

        var skipped = new List<string>();
        var combinations = ParameterGridSearchEngine.Expand(gridParameters, baseline, OptimizerParameterCatalog.Clone, OptimizerParameterCatalog.Validate, skipped);
        if (combinations.Count == 0)
        {
            Console.Error.WriteLine("Every combination violates a parameter invariant:");
            foreach (var s in skipped.Take(5)) Console.Error.WriteLine($"  {s}");
            return 1;
        }

        var context = await BacktestDataContext.LoadAsync(instruments, bars, classifications, surveillance, benchmarkOptions.Value, from, to, logger, cancellationToken, refinementMode: backtestOptions.Value.SignalToOrderLag,
            deliveries: deliveryRepo, scoring: stockScoringOptions.Value, strategy: strategyOptions.Value);
        if (context is null)
        {
            Console.Error.WriteLine("No cached bars available for the requested range — run 'dotnet run -- fetch' (or 'download') first.");
            return 1;
        }

        Console.WriteLine("========================================================");
        Console.WriteLine($" OPTIMIZE — {combinations.Count} combinations over: {string.Join(", ", requested.Select(r => r.Name))}");
        if (skipped.Count > 0) Console.WriteLine($" Skipped {skipped.Count} combination(s) that break a parameter invariant (not repaired).");
        Console.WriteLine($" Train: {from:yyyy-MM-dd}..{trainEnd:yyyy-MM-dd}   Validate: {validateStart:yyyy-MM-dd}..{to:yyyy-MM-dd}");
        Console.WriteLine("========================================================");

        var minTrades = 30;
        var evaluated = new List<OptimizationCandidate>(combinations.Count);
        for (var i = 0; i < combinations.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (values, options) = combinations[i];
            Console.Error.Write($"\r  [{i + 1}/{combinations.Count}] scoring...   ");
            var train = BacktestEngine.Run(context.BuildRequest(from, trainEnd, StrategyVersionTag, "optimize-train"), options, cancellationToken);
            var validate = BacktestEngine.Run(context.BuildRequest(validateStart, to, StrategyVersionTag, "optimize-validate"), options, cancellationToken);
            evaluated.Add(OptimizationCandidateEvaluator.Evaluate(
                values, train, validate, from, trainEnd, validateStart, to,
                options.Backtest.MaxAcceptableDrawdownPercent, minTrades, options.MarketRegime.AggressiveThreshold));
        }
        Console.Error.WriteLine();

        var dimensions = requested.Select(r => new GridDimension(r.Name, r.Values)).ToList();
        var ranked = OptimizationRanker.Rank(evaluated, dimensions);
        PrintRanking(ranked, requested.Select(r => r.Name).ToList(), parsed.Top);

        var winner = ranked.FirstOrDefault(r => !r.Rejected);
        Console.WriteLine();
        if (winner is null)
        {
            Console.WriteLine("NO ACCEPTABLE CANDIDATE: every combination was rejected by the anti-overfitting check (spec §5.7). Do not adopt any of these.");
        }
        else
        {
            Console.WriteLine($"Best robust candidate: {string.Join(", ", winner.Candidate.Parameters.Select(kv => $"{kv.Key}={kv.Value}"))}  (robust score {winner.RobustScore:F2}, {winner.NeighborCount} neighbor(s))");
            foreach (var f in winner.Candidate.Overfitting.Flags.Where(f => f.Triggered)) Console.WriteLine($"  WARNING {f.Name}: {f.Detail}");
            if (winner.NeighborCount == 0) Console.WriteLine("  WARNING: no neighboring grid cells — robustness could not be assessed; sweep at least 3 values per dimension.");
        }
        Console.WriteLine("Validate is the slice this winner was chosen on — confirm with 'dotnet run -- walkforward' after applying the values in config.");
        Console.WriteLine("========================================================");

        string? batchId = null;
        if (runRepository is not null)
        {
            try
            {
                batchId = await runRepository.InsertBatchAsync(ranked.Select(ToRecord).ToList(), cancellationToken);
                Console.WriteLine($"Stored {ranked.Count} candidate(s) in OptimizationRuns (BatchId {batchId}).");
            }
            catch (Exception ex) { logger.LogWarning(ex, "optimize: persisting OptimizationRuns failed."); Console.Error.WriteLine("Warning: could not persist the candidates to OptimizationRuns (see log)."); }
        }

        var report = OptimizationReport.Build(ranked.Select(r => new OptimizationReportRow(r.Candidate.Parameters, r.RobustScore)).ToList());
        try
        {
            var dir = RepoPaths.Combine(pathsOptions.Value.Reports);
            Directory.CreateDirectory(dir);
            var prefix = Path.Combine(dir, $"optimize-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
            await File.WriteAllTextAsync($"{prefix}.json", report.ToJson(), cancellationToken);
            await File.WriteAllTextAsync($"{prefix}.csv", report.ToCsv(), cancellationToken);
            await File.WriteAllTextAsync($"{prefix}.html", report.ToHtml(), cancellationToken);
            Console.WriteLine($"Exported: {prefix}.{{json,csv,html}}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "optimize: report export failed.");
        }

        logger.LogInformation("optimize: {Count} combinations scored (batch {Batch}), best robust score {Score}.", ranked.Count, batchId ?? "unsaved", winner?.RobustScore);
        return winner is null ? 2 : 0;
    }

    internal static OptimizationRunRecord ToRecord(RankedOptimizationCandidate r) => new()
    {
        ParameterSetJson = JsonSerializer.Serialize(r.Candidate.Parameters),
        CompositeScoreBreakdownJson = JsonSerializer.Serialize(new
        {
            rank = r.Rank, trainScore = r.Candidate.TrainScore, validateScore = r.Candidate.ValidateScore, selectionScore = r.SelectionScore,
            robustScore = r.RobustScore, neighborMeanScore = r.NeighborMeanScore, neighborCount = r.NeighborCount, rejected = r.Rejected,
            validateTrades = r.Candidate.ValidateTradeCount, validateMaxDrawdownPercent = r.Candidate.ValidateMaxDrawdownPercent,
            triggeredFlags = r.Candidate.Overfitting.Flags.Where(f => f.Triggered).Select(f => f.Name).ToArray(),
        }),
        CompositeScore = r.RobustScore,
        IsOutOfSample = false, // the Validate slice is what the winner was selected on
    };

    private static void PrintRanking(IReadOnlyList<RankedOptimizationCandidate> ranked, IReadOnlyList<string> names, int top)
    {
        Console.WriteLine($"{"Rank",-5}{string.Join("", names.Select(n => n.Length > 18 ? n[..18].PadRight(20) : n.PadRight(20)))}{"Train",9}{"Validate",10}{"Robust",9}{"Trades",8}{"MaxDD%",9}  Status");
        foreach (var r in ranked.Take(top))
        {
            var c = r.Candidate;
            var triggered = c.Overfitting.Flags.Where(f => f.Triggered).Select(f => f.Name).ToList();
            var status = r.Rejected ? "REJECTED " + string.Join(",", triggered.Where(n => n is "ExcessiveDrawdown" or "OutOfSamplePerformanceDisappears")) : (triggered.Count > 0 ? $"warn: {string.Join(",", triggered)}" : "ok");
            Console.WriteLine($"{r.Rank,-5}{string.Join("", names.Select(n => c.Parameters[n].ToString("0.####", System.Globalization.CultureInfo.InvariantCulture).PadRight(20)))}{c.TrainScore,9:F1}{c.ValidateScore,10:F1}{r.RobustScore,9:F1}{c.ValidateTradeCount,8}{c.ValidateMaxDrawdownPercent,9:F1}  {status}");
        }
        if (ranked.Count > top) Console.WriteLine($"  ... {ranked.Count - top} more (all are in the report and OptimizationRuns; --top N shows more)");
    }

    private static void PrintCatalog()
    {
        Console.WriteLine("Sweepable parameters (use: optimize --param Name=v1,v2,v3 [--param ...]); default value ladder in brackets:");
        foreach (var p in OptimizerParameterCatalog.All)
            Console.WriteLine($"  {p.Name,-28}[{string.Join(", ", p.DefaultValues)}]  {p.Description}");
        Console.WriteLine("Not sweepable: RSI and VCP-score thresholds — the breakout gate has no such conditions, so a sweep would change nothing.");
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
}
