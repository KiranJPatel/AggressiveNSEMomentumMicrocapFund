using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Scanner;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// `dotnet run -- scan` (spec §7.1, §7.3) — runs ScannerEngine (Tranche 14)
/// against the locally-cached universe, persists the ranked results, and
/// prints the top candidates. Requires bars already cached via `fetch` for
/// every instrument to be scanned (and for the benchmark indices) — this
/// command reads local SQLite only, it never calls Yahoo itself.
/// </summary>
public static class ScanCommand
{
    public static async Task<int> RunAsync(
        IInstrumentRepository instruments, IHistoricalBarRepository bars, IUniverseClassificationRepository classifications,
        ISurveillanceRepository surveillance, IScannerResultRepository results,
        IOptions<ScannerOptions> scannerOptions, IOptions<IndicatorOptions> indicatorOptions, IOptions<VcpOptions> vcpOptions,
        IOptions<RsOptions> rsOptions, IOptions<StockScoringOptions> stockScoringOptions, IOptions<StrategyOptions> strategyOptions,
        IOptions<MarketRegimeOptions> marketRegimeOptions, IOptions<RiskOptions> riskOptions, IOptions<PortfolioOptions> portfolioOptions,
        IOptions<BenchmarkOptions> benchmarkOptions, ILogger logger, CancellationToken cancellationToken = default,
        IFundamentalDataProvider? fundamentals = null, IInstitutionalActivityProvider? institutional = null, bool explain = false,
        IMarketRegimeRepository? regimes = null, ITradeRepository? trades = null, IScanEvidenceRepository? evidenceRepo = null, IDeliveryDataRepository? deliveryRepo = null)
    {
        var engineOptions = new ScannerEngineOptions
        {
            Scanner = scannerOptions.Value, Indicator = indicatorOptions.Value, Vcp = vcpOptions.Value, Rs = rsOptions.Value,
            StockScoring = stockScoringOptions.Value, Strategy = strategyOptions.Value, MarketRegime = marketRegimeOptions.Value,
            Risk = riskOptions.Value, Portfolio = portfolioOptions.Value,
        };

        // portfolioContext is null here by design: `scan` is a stateless universe ranking, so SuggestedEntry/Stop/
        // Risk/PositionSize show as 0 rather than a fabricated sizing. Position-sized candidates come from the
        // `paper`/`live` cycle, which supplies a real portfolio context.
        var outcome = await ScanCycle.RunAsync(instruments, bars, classifications, surveillance, results, engineOptions, benchmarkOptions.Value, logger, cancellationToken, fundamentals, institutional, regimes, trades, evidenceRepo, deliveryRepo);
        if (outcome.Error is not null)
        {
            Console.Error.WriteLine(outcome.Error);
            return 1;
        }
        var ranked = outcome.Ranked;
        var asOfDate = outcome.AsOfDate;
        var scannedCount = outcome.LoadedCount;
        // Always explain an EMPTY scan (the most common first-run surprise); --explain also shows why symbols were dropped when some ranked.
        if (explain || ranked.Count == 0) PrintRejections(outcome.Rejections, scannedCount, explain);

        Console.WriteLine("========================================================");
        Console.WriteLine($" SCAN RESULTS — as of {asOfDate:yyyy-MM-dd} — top {ranked.Count} of {scannedCount} scanned");
        Console.WriteLine("========================================================");
        if (outcome.Regime is { } rg)
            Console.WriteLine($" Regime: {rg.Classification} (score {rg.RegimeScore}, deployable {rg.DeployableCapitalPercent:F0}%)  >50DMA: {Fmt(rg.PercentUniverseAbove50Dma)}  >200DMA: {Fmt(rg.PercentUniverseAbove200Dma)}  A/D: {Fmt(rg.AdvanceDeclineRatio)}  breakout success: {Fmt(rg.BreakoutSuccessRate)}  weekly bias: {rg.WeeklyBias}");
        Console.WriteLine($"{"Rank",-5}{"Symbol",-14}{"Score",-8}{"RS",-7}{"VCP",-7}{"Price",-10}{"Pivot",-10}{"Status",-14}Dist%");
        foreach (var r in ranked)
        {
            Console.WriteLine(
                $"{r.Rank,-5}{r.Symbol,-14}{r.Score,-8:F1}{r.RelativeStrengthScore,-7:F1}{r.VcpScore,-7:F1}{r.Price,-10:N2}{r.PivotPrice,-10:N2}{r.BreakoutStatus,-14}{r.DistanceFromPivotPercent:F2}%");
        }
        if (explain && outcome.Evidence is { Count: > 0 } evidence)
        {
            // Spec §3.4 price structure for the ranked symbols (informational: it does not change the ranking).
            Console.WriteLine();
            Console.WriteLine($"{"Symbol",-14}{"Structure",-14}{"Support",-12}{"Resistance",-12}Last swing high / low");
            foreach (var e in evidence.Where(x => x.Structure is not null))
            {
                var st = e.Structure!;
                Console.WriteLine($"{e.Symbol,-14}{st.State,-14}{(st.Support is { } sp ? sp.ToString("N2") : "-"),-12}{(st.Resistance is { } rs ? rs.ToString("N2") : "none"),-12}{st.LastHighLabel ?? "?"} / {st.LastLowLabel ?? "?"}");
            }
        }
        Console.WriteLine("========================================================");

        logger.LogInformation("scan: {ScannedCount} symbols evaluated, {RankedCount} ranked results persisted as of {AsOfDate}, scan duration {ElapsedMs}ms.", scannedCount, ranked.Count, asOfDate, (long)outcome.Elapsed.TotalMilliseconds);
        return 0;
    }

    private static string Fmt(decimal? v) => v is null ? "n/a" : v.Value.ToString("F1");

    private static void PrintRejections(IReadOnlyList<ScanRejection>? rejections, int scanned, bool explain)
    {
        if (rejections is null || rejections.Count == 0) return;
        var rejectedSymbols = rejections.Select(r => r.Symbol).Distinct().Count();
        Console.WriteLine($"Rejected before ranking: {rejectedSymbols} of {scanned} symbol(s).");
        foreach (var g in rejections.GroupBy(r => r.Gate).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-26} {g.Count(),4} symbol(s)  e.g. {g.First().Symbol}: {g.First().Detail}");
        if (!explain) { Console.WriteLine("  (per-symbol detail: re-run with --explain)"); return; }
        foreach (var r in rejections.OrderBy(r => r.Symbol).ThenBy(r => r.Gate))
            Console.WriteLine($"    {r.Symbol,-14} {r.Gate,-24} {r.Detail}");
    }
}
