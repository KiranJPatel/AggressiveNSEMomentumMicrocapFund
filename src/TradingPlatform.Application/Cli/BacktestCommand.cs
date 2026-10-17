using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingPlatform.Application.Infrastructure;
using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Reporting.Reports;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// `dotnet run -- backtest [--from --to]` (spec §5, §7.3, §8.1) — assembles
/// a BacktestRunRequest from local SQLite (Instruments/HistoricalBars/
/// UniverseClassifications/SurveillanceStatuses), runs BacktestEngine
/// (Tranche 11), persists the run/trades/snapshots/drawdown-events, and
/// prints the §8.1 console report. Requires bars already cached via
/// `fetch` for every instrument in scope plus both benchmark indices —
/// this command reads local SQLite only, it never calls Yahoo itself.
/// </summary>
public static class BacktestCommand
{
    private const int WarmupCalendarDays = 500;
    private const string StrategyVersionTag = "AggressiveMomentumV1-Tranche18";

    public static async Task<int> RunAsync(
        string[] args, IInstrumentRepository instruments, IHistoricalBarRepository bars,
        IUniverseClassificationRepository classifications, ISurveillanceRepository surveillance,
        IBacktestRunRepository runRepo, ITradeRepository tradeRepo,
        IOptions<IndicatorOptions> indicatorOptions, IOptions<VcpOptions> vcpOptions, IOptions<RsOptions> rsOptions,
        IOptions<StockScoringOptions> stockScoringOptions, IOptions<StrategyOptions> strategyOptions,
        IOptions<MarketRegimeOptions> marketRegimeOptions, IOptions<ExitOptions> exitOptions, IOptions<RiskOptions> riskOptions,
        IOptions<PortfolioOptions> portfolioOptions, IOptions<BacktestOptions> backtestOptions, IOptions<BenchmarkOptions> benchmarkOptions,
        IOptions<PathsOptions> pathsOptions, ILogger logger, CancellationToken cancellationToken = default,
        IDeliveryDataRepository? deliveryRepo = null)
    {
        var (from, to, parseError) = ParseDateRange(args);
        if (parseError is not null)
        {
            Console.Error.WriteLine(parseError);
            return 1;
        }

        var barsFrom = from.AddDays(-WarmupCalendarDays);

        var (broadBars, midcapBars) = await UniverseDataLoader.LoadBenchmarksAsync(bars, benchmarkOptions.Value, barsFrom, to, logger, cancellationToken);
        if (broadBars.Count == 0)
        {
            Console.Error.WriteLine($"No cached bars for the broad-index benchmark ({benchmarkOptions.Value.BroadIndexSymbol}). Run 'dotnet run -- fetch --symbol {benchmarkOptions.Value.BroadIndexSymbol} --from {barsFrom:yyyy-MM-dd} --to {to:yyyy-MM-dd}' first.");
            return 1;
        }

        // Point-in-time universe/surveillance history isn't ingested by
        // anything in this codebase yet (see docs/gap-log.md) — so
        // classification/surveillance are necessarily resolved as of the
        // run's END date, applied across the whole run. That is exactly
        // what SurvivorshipBiased=true (below) exists to disclose.
        var loaded = await UniverseDataLoader.LoadActiveUniverseAsync(instruments, bars, classifications, surveillance, barsFrom, to, to, logger, cancellationToken);
        if (loaded.Count == 0)
        {
            Console.Error.WriteLine("No active instruments have any cached bars in range. Run 'dotnet run -- fetch --symbol <SYMBOL> --from ... --to ...' for at least one instrument first.");
            return 1;
        }

        var engineOptions = new BacktestEngineOptions
        {
            Indicator = indicatorOptions.Value,
            Vcp = vcpOptions.Value,
            Rs = rsOptions.Value,
            StockScoring = stockScoringOptions.Value,
            Strategy = strategyOptions.Value,
            MarketRegime = marketRegimeOptions.Value,
            Exit = exitOptions.Value,
            Risk = riskOptions.Value,
            Portfolio = portfolioOptions.Value,
            Backtest = backtestOptions.Value,
        };

        var configurationHash = ComputeConfigurationHash(engineOptions);
        var latestBarDate = loaded.SelectMany(s => s.Bars).Select(b => b.Timestamp).Concat(broadBars.Select(b => b.Timestamp)).Max();
        var totalBarCount = loaded.Sum(s => s.Bars.Count) + broadBars.Count + (midcapBars?.Count ?? 0);
        var dataVersion = $"sqlite:{totalBarCount}bars:latest={latestBarDate:yyyy-MM-dd}";

        var symbolContexts = loaded.Select(s => new BacktestSymbolContext
        {
            Symbol = s.Instrument.Symbol,
            Bars = s.Bars,
            MarketCapBucket = s.MarketCapBucket,
            Sector = s.Classification?.Sector ?? s.Instrument.Sector,
            HasCriticalSurveillanceFlag = UniverseDataLoader.HasCriticalSurveillanceFlag(s.Surveillance),
            HasSevereDataQualityIssue = UniverseDataLoader.HasSevereDataQualityIssue(s.Surveillance),
        }).ToList();

        // Spec §3.8/§7.3: intraday refinement is opt-in (Backtest:SignalToOrderLag = Refinement60m | Refinement15m). Sessions without cached
        // intraday bars follow Backtest:RefinementSkipWhenNoIntradayData and are counted in the report below.
        var (refinedContexts, intradayCoverage) = await IntradayContextLoader.AttachAsync(symbolContexts, bars, backtestOptions.Value.SignalToOrderLag, from, to, logger, cancellationToken);
        symbolContexts = refinedContexts.ToList();
        // Spec §3.4 delivery %: loaded only when scoring or the breakout gate uses it (StockScoring:UseDeliveryPercent / Strategy:BreakoutMinDeliveryRatio).
        symbolContexts = (await DeliveryContextLoader.AttachToBacktestAsync(symbolContexts, deliveryRepo, stockScoringOptions.Value, strategyOptions.Value, from, to, logger, cancellationToken)).ToList();
        if (intradayCoverage is { SymbolsWithHourly: 0 })
            Console.Error.WriteLine("Warning: SignalToOrderLag is a refinement mode but no 60m bars are cached — every entry will " +
                (backtestOptions.Value.RefinementSkipWhenNoIntradayData ? "be SKIPPED" : "fall back to the daily open") + ". Run 'download-intraday --timeframe 60m' first.");

        var startedAt = DateTimeOffset.UtcNow;
        var pendingRun = new BacktestRun
        {
            StrategyVersion = StrategyVersionTag,
            ConfigurationHash = configurationHash,
            DataVersion = dataVersion,
            StartDate = from,
            EndDate = to,
            Status = BacktestRunStatus.Pending,
            SurvivorshipBiased = true,
            StartedAt = startedAt,
        };
        var runId = await runRepo.InsertStartedAsync(pendingRun, cancellationToken);
        logger.LogInformation("backtest: started run {RunId} ({From}..{To}), {SymbolCount} symbols, config hash {ConfigHash}.", runId, from, to, symbolContexts.Count, configurationHash);

        var request = new BacktestRunRequest
        {
            StrategyVersion = StrategyVersionTag,
            ConfigurationHash = configurationHash,
            DataVersion = dataVersion,
            StartDate = from,
            EndDate = to,
            SurvivorshipBiased = true,
            BroadIndexBars = broadBars,
            MidcapIndexBars = midcapBars,
            Symbols = symbolContexts,
        };

        BacktestRunResult result;
        var backtestStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            result = BacktestEngine.Run(request, engineOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A cancelled run has no results; leave no run stuck in "Running" (the closest terminal status is Failed).
            logger.LogWarning("backtest: run {RunId} cancelled by the user.", runId);
            await runRepo.MarkFailedAsync(runId, DateTimeOffset.UtcNow, CancellationToken.None);
            Console.Error.WriteLine($"Backtest cancelled (run {runId} marked Failed; no results were stored).");
            return 130;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "backtest: run {RunId} failed during simulation.", runId);
            await runRepo.MarkFailedAsync(runId, DateTimeOffset.UtcNow, cancellationToken);
            Console.Error.WriteLine($"Backtest failed: {ex.Message}");
            return 1;
        }
        backtestStopwatch.Stop();

        // Correlate each Trade with its own legs via the engine's in-memory
        // TradeId sequence (BacktestPortfolioSimulator.AllocateTradeId) —
        // that id is meaningful for THIS correlation even though it isn't a
        // real database id yet; InsertAsync assigns the real one per trade.
        var dbTradeIds = new List<long>(result.Trades.Count);
        foreach (var trade in result.Trades)
        {
            var legsForTrade = result.TradeLegs.Where(l => l.TradeId == trade.Id).OrderBy(l => l.FilledAt).ToList();
            dbTradeIds.Add(await tradeRepo.InsertAsync(trade, legsForTrade, cancellationToken));
        }
        if (dbTradeIds.Count > 0) await runRepo.LinkTradesAsync(runId, dbTradeIds, cancellationToken);
        await runRepo.InsertSnapshotsAsync(runId, result.DailySnapshots, cancellationToken);
        if (result.DrawdownEvents.Count > 0) await runRepo.InsertDrawdownEventsAsync(runId, result.DrawdownEvents, cancellationToken);

        result.Run.Id = runId;
        await runRepo.UpdateCompletedAsync(result.Run, cancellationToken);

        var report = BacktestReport.Build(result, portfolioOptions.Value.InitialCapital, backtestOptions.Value.MaxAcceptableDrawdownPercent);
        Console.WriteLine(report.ToConsoleText());

        await WriteExportsAsync(pathsOptions.Value, runId, report, logger, cancellationToken);

        logger.LogInformation(
            "backtest: run {RunId} completed in {ElapsedMs}ms. Trades={TradeCount} FinalCapital={FinalCapital} MaxDrawdown={MaxDrawdown}%.",
            runId, backtestStopwatch.ElapsedMilliseconds, result.Run.TradeCount, result.Run.FinalCapital, result.Run.MaxDrawdownPercent);
        return 0;
    }

    private static async Task WriteExportsAsync(PathsOptions paths, long runId, BacktestReport report, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var dir = RepoPaths.Combine(paths.Backtests);
            Directory.CreateDirectory(dir);
            var prefix = Path.Combine(dir, $"backtest-{runId}");
            await File.WriteAllTextAsync($"{prefix}.json", report.ToJson(), cancellationToken);
            await File.WriteAllTextAsync($"{prefix}.csv", report.ToCsv(), cancellationToken);
            await File.WriteAllTextAsync($"{prefix}.html", report.ToHtml(), cancellationToken);
            Console.WriteLine($"Exported: {prefix}.{{json,csv,html}}");
        }
        catch (Exception ex)
        {
            // Exports are a convenience on top of the DB record, which is
            // already durable at this point — a write failure here (e.g. a
            // read-only /backtests mount) must not make the run look failed.
            logger.LogWarning(ex, "backtest: run {RunId} completed but exporting report files failed.", runId);
        }
    }

    private static string ComputeConfigurationHash(BacktestEngineOptions options)
    {
        var json = JsonSerializer.Serialize(options);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
    }

    internal static (DateOnly From, DateOnly To, string? Error) ParseDateRange(string[] args)
    {
        DateOnly? from = null, to = null;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--from")
            {
                if (!DateOnly.TryParse(args[i + 1], out var f))
                    return (default, default, $"--from value '{args[i + 1]}' is not a valid date (expected yyyy-MM-dd).");
                from = f;
            }
            if (args[i] == "--to")
            {
                if (!DateOnly.TryParse(args[i + 1], out var t))
                    return (default, default, $"--to value '{args[i + 1]}' is not a valid date (expected yyyy-MM-dd).");
                to = t;
            }
        }

        // Spec brackets [--from --to] as optional — default to the last
        // three full years ending yesterday, a sensible out-of-the-box
        // window rather than requiring the operator to know one up front.
        var resolvedTo = to ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var resolvedFrom = from ?? resolvedTo.AddYears(-3);

        if (resolvedFrom >= resolvedTo)
            return (resolvedFrom, resolvedTo, $"--from ({resolvedFrom:yyyy-MM-dd}) must be before --to ({resolvedTo:yyyy-MM-dd}).");

        return (resolvedFrom, resolvedTo, null);
    }
}
