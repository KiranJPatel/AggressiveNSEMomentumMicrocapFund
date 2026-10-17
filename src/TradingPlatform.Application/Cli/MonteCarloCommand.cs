using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Backtesting.Selection;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Reporting.Reports;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// `dotnet run -- montecarlo --run-id N [--iterations N] [--seed N] [--mode reshuffle|bootstrap|both]`
/// (spec §5.5, §7.3, §8.3) — Monte Carlo risk-of-ruin/percentile analysis
/// (Tranche 13's MonteCarloSimulator) on an EXISTING backtest run's
/// completed trades, rather than re-running a whole backtest inline: `run
/// backtest` first, then `montecarlo --run-id` against its output — same
/// separation of concerns as spec's own listed run commands ("run a
/// sample backtest, run walk-forward, run Monte Carlo" as distinct steps).
/// </summary>
public static class MonteCarloCommand
{
    public static async Task<int> RunAsync(
        string[] args, IBacktestRunRepository runs, IOptions<PortfolioOptions> portfolioOptions, IOptions<PathsOptions> pathsOptions,
        ILogger logger, CancellationToken cancellationToken = default)
    {
        var runId = ParseLongArg(args, "--run-id", out var runIdInvalid);
        if (runIdInvalid)
        {
            Console.Error.WriteLine("--run-id value must be a valid integer.");
            return 1;
        }
        if (runId is null)
        {
            Console.Error.WriteLine("montecarlo requires --run-id <N> (the Id of a run already produced by 'dotnet run -- backtest'; see 'dotnet run -- serve' + GET /api/backtests to list them).");
            return 1;
        }

        var iterationsRaw = ParseLongArg(args, "--iterations", out var iterationsInvalid);
        if (iterationsInvalid)
        {
            Console.Error.WriteLine("--iterations value must be a valid integer.");
            return 1;
        }
        var iterations = iterationsRaw ?? 1000;
        if (iterations is < 1 or > MaxIterations)
        {
            Console.Error.WriteLine($"--iterations must be between 1 and {MaxIterations:N0} (got {iterations:N0}).");
            return 1;
        }

        var seed = ParseLongArg(args, "--seed", out var seedInvalid);
        if (seedInvalid)
        {
            Console.Error.WriteLine("--seed value must be a valid integer.");
            return 1;
        }

        var modes = ParseModes(args, out var modeError);
        if (modeError is not null) { Console.Error.WriteLine(modeError); return 1; }

        var run = await runs.GetRunAsync(runId.Value, cancellationToken);
        if (run is null)
        {
            Console.Error.WriteLine($"No backtest run with Id={runId.Value} found.");
            return 1;
        }

        var trades = await runs.GetTradesAsync(runId.Value, cancellationToken);
        if (trades.Count == 0)
        {
            Console.Error.WriteLine($"Backtest run {runId.Value} produced zero trades — nothing to simulate (MonteCarloSimulator requires at least one).");
            return 1;
        }

        var initialCapital = portfolioOptions.Value.InitialCapital;

        Console.WriteLine("========================================================");
        Console.WriteLine($" MONTE CARLO — backtest run {runId.Value} ({trades.Count} trades, {iterations:N0} iterations{(seed is null ? "" : $", seed {seed}")})");
        Console.WriteLine("========================================================");

        foreach (var mode in modes)
        {
            // Each mode gets its own generator from the same seed, so --seed reproduces a mode's numbers whether it runs alone or with the other.
            var random = seed is null ? new Random() : new Random(SeedToInt(seed.Value));
            var result = MonteCarloSimulator.Simulate(trades, initialCapital, (int)iterations, random, mode: mode);
            var report = MonteCarloReport.Build(result, initialCapital);

            Console.WriteLine(mode == MonteCarloMode.Reshuffle
                ? "[Reshuffle] same trades, different order — measures PATH risk (drawdown/ruin); ending capital barely moves by construction."
                : "[Bootstrap] trades resampled with replacement, returns compounded — measures OUTCOME uncertainty.");
            Console.WriteLine($"Ending capital  P5={result.P5EndingCapital:N0}  P25={result.P25EndingCapital:N0}  Median={result.MedianEndingCapital:N0}  P75={result.P75EndingCapital:N0}  P95={result.P95EndingCapital:N0}");
            Console.WriteLine($"P(Drawdown>20%)={result.ProbabilityDrawdownExceeds20Percent:F1}%  P(Drawdown>30%)={result.ProbabilityDrawdownExceeds30Percent:F1}%  P(Drawdown>40%)={result.ProbabilityDrawdownExceeds40Percent:F1}%");
            Console.WriteLine($"Probability of ruin (>50% loss): {result.ProbabilityOfRuin:F1}%");
            Console.WriteLine();

            try
            {
                var dir = RepoPaths.Combine(pathsOptions.Value.Backtests);
                Directory.CreateDirectory(dir);
                var prefix = Path.Combine(dir, $"montecarlo-{runId.Value}-{mode.ToString().ToLowerInvariant()}");
                await File.WriteAllTextAsync($"{prefix}.json", report.ToJson(), cancellationToken);
                await File.WriteAllTextAsync($"{prefix}.csv", report.ToCsv(), cancellationToken);
                await File.WriteAllTextAsync($"{prefix}.html", report.ToHtml(), cancellationToken);
                Console.WriteLine($"Exported: {prefix}.{{json,csv,html}}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "montecarlo: report export failed for run {RunId} ({Mode}).", runId.Value, mode);
            }
            logger.LogInformation("montecarlo: run {RunId} {Mode}, {Iterations} iterations, P(ruin)={ProbabilityOfRuin}%.", runId.Value, mode, iterations, result.ProbabilityOfRuin);
        }
        Console.WriteLine("Capital-growth scenarios in the exported reports are hypothetical mathematical projections, not forecasts (spec §8.3).");
        Console.WriteLine("========================================================");
        return 0;
    }

    private const long MaxIterations = 1_000_000;

    /// <summary>Any 64-bit seed maps deterministically to the 32-bit seed System.Random takes (a plain (int) cast silently wrapped).</summary>
    internal static int SeedToInt(long seed) => unchecked((int)(seed ^ (seed >> 32)));

    internal static IReadOnlyList<MonteCarloMode> ParseModes(string[] args, out string? error)
    {
        error = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] != "--mode") continue;
            var value = i + 1 < args.Length ? args[i + 1].Trim().ToLowerInvariant() : "";
            switch (value)
            {
                case "reshuffle": return new[] { MonteCarloMode.Reshuffle };
                case "bootstrap": return new[] { MonteCarloMode.Bootstrap };
                case "both": return new[] { MonteCarloMode.Reshuffle, MonteCarloMode.Bootstrap };
                default:
                    error = $"--mode must be reshuffle, bootstrap or both (got '{(i + 1 < args.Length ? args[i + 1] : "")}').";
                    return Array.Empty<MonteCarloMode>();
            }
        }
        return new[] { MonteCarloMode.Reshuffle, MonteCarloMode.Bootstrap };
    }

    internal static long? ParseLongArg(string[] args, string flag, out bool invalid)
    {
        invalid = false;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] != flag) continue;
            if (!long.TryParse(args[i + 1], out var v))
            {
                invalid = true;
                return null;
            }
            return v;
        }
        return null;
    }
}
