using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;
using TradingPlatform.Scanner;
using TradingPlatform.Scanner.Scheduling;

namespace TradingPlatform.Application.Cli;

/// <summary>Result of one scan cycle. <see cref="Error"/> is non-null (and Ranked empty) when the cycle could not run at all.</summary>
public sealed record ScanCycleOutcome(IReadOnlyList<ScannerResult> Ranked, int LoadedCount, DateOnly AsOfDate, TimeSpan Elapsed, string? Error, IReadOnlyList<ScanRejection>? Rejections = null, MarketRegimeSnapshot? Regime = null, IReadOnlyList<ScanEvidence>? Evidence = null);

/// <summary>
/// The one implementation of "load cached universe -> ScannerEngine.Scan -> persist", shared by the one-shot
/// `scan` command and the scheduled <see cref="ScanRunner"/>. Extracted so the two can never drift apart —
/// before this existed, ScannerBackgroundService (spec §7.1) had no IScanRunner implementation at all.
/// </summary>
public static class ScanCycle
{
    private const int WarmupCalendarDays = 500;

    internal static DateOnly Max(DateOnly a, DateOnly b) => a >= b ? a : b;

    public static async Task<ScanCycleOutcome> RunAsync(
        IInstrumentRepository instruments, IHistoricalBarRepository bars, IUniverseClassificationRepository classifications,
        ISurveillanceRepository surveillance, IScannerResultRepository results,
        ScannerEngineOptions engineOptions, BenchmarkOptions benchmarkOptions, ILogger logger, CancellationToken cancellationToken,
        IFundamentalDataProvider? fundamentals = null, IInstitutionalActivityProvider? institutional = null,
        IMarketRegimeRepository? regimes = null, ITradeRepository? trades = null, IScanEvidenceRepository? evidenceRepo = null,
        IDeliveryDataRepository? deliveryRepo = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-WarmupCalendarDays);
        var empty = Array.Empty<ScannerResult>();

        var (broadBars, midcapBars) = await UniverseDataLoader.LoadBenchmarksAsync(bars, benchmarkOptions, from, today, logger, cancellationToken);
        if (broadBars.Count == 0)
            return new ScanCycleOutcome(empty, 0, today, TimeSpan.Zero,
                $"No cached bars for the broad-index benchmark ({benchmarkOptions.BroadIndexSymbol}). Run 'dotnet run -- fetch --symbol {benchmarkOptions.BroadIndexSymbol} --from ... --to ...' first.");

        // "As of" the most recent bar actually on hand, not blindly "today": a stale cache scans as of its own
        // last date rather than silently treating missing recent history as "the market did nothing".
        var asOfDate = DateOnly.FromDateTime(broadBars[^1].Timestamp.Date);

        // Classification / surveillance / fundamentals describe the CURRENT state of each stock, so they are looked up as of "today" (the
        // market's date), not as of the last bar's date: otherwise a status recorded this morning would be "from the future" and every
        // symbol would read Unknown until tonight's bar arrives. (Backtests keep strict point-in-time lookups; they do not use this path.)
        var lookupDate = Max(asOfDate, RealtimeBarOverlay.MarketDate(DateTimeOffset.UtcNow, engineOptions.Scanner));
        var loaded = await UniverseDataLoader.LoadActiveUniverseAsync(instruments, bars, classifications, surveillance, from, today, lookupDate, logger, cancellationToken, engineOptions.Scanner.SurveillanceMaxStalenessDays);
        if (loaded.Count == 0)
            return new ScanCycleOutcome(empty, 0, asOfDate, TimeSpan.Zero,
                "No active instruments have any cached bars. Run 'dotnet run -- fetch --symbol <SYMBOL> --from ... --to ...' for at least one instrument first.");

        var contexts = new List<ScannerSymbolContext>(loaded.Count);
        foreach (var s in loaded)
        {
            var (fund, inst, priorFund) = await ScannerEnrichment.LoadAsync(fundamentals, institutional, s.Instrument.Symbol, lookupDate, cancellationToken);
            contexts.Add(new ScannerSymbolContext
            {
                Instrument = s.Instrument, Bars = s.Bars, Surveillance = s.Surveillance, Classification = s.Classification,
                Fundamentals = fund, PriorFundamentals = priorFund, InstitutionalActivity = inst,
                DeliveryRecords = await DeliveryContextLoader.LoadForScanAsync(deliveryRepo, s.Instrument.Symbol, lookupDate, engineOptions.StockScoring, engineOptions.Strategy, cancellationToken),
            });
        }

        cancellationToken.ThrowIfCancellationRequested(); // after loading, before ranking: nothing has been written yet
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rejections = new List<ScanRejection>();
        // Breakout-success-rate input (spec §3.1) comes from the paper/live journal; none yet => that component is simply unavailable.
        var scanOptions = trades is null ? engineOptions : engineOptions.WithRecentClosedTrades(await trades.GetLiveTradesAsync(cancellationToken));
        var outcome = ScannerEngine.ScanDetailed(asOfDate, broadBars, midcapBars, contexts, portfolioContext: null, scanOptions, rejections);
        var ranked = outcome.Results;
        sw.Stop();

        await results.InsertCycleAsync(ranked, cancellationToken);
        if (regimes is not null) await regimes.UpsertAsync(outcome.Regime, cancellationToken);
        if (evidenceRepo is not null) await evidenceRepo.UpsertAsync(outcome.Evidence, cancellationToken);
        return new ScanCycleOutcome(ranked, loaded.Count, asOfDate, sw.Elapsed, null, rejections, outcome.Regime, outcome.Evidence);
    }
}

/// <summary>
/// Optional per-symbol fundamentals / institutional-activity enrichment (spec §2.4). A snapshot whose availability is
/// still Unknown (nothing ingested) is passed as NULL — exactly what the engines received before providers existed —
/// so unimported data can never change a score; only data someone actually imported does.
/// </summary>
public static class ScannerEnrichment
{
    public static async Task<(FundamentalSnapshot? Fundamentals, InstitutionalActivitySnapshot? Institutional, FundamentalSnapshot? Prior)> LoadAsync(
        IFundamentalDataProvider? fundamentals, IInstitutionalActivityProvider? institutional, string symbol, DateOnly asOf, CancellationToken ct)
    {
        FundamentalSnapshot? f = null, prior = null; InstitutionalActivitySnapshot? i = null;
        if (fundamentals is not null)
        {
            var snap = await fundamentals.GetFundamentalsAsync(symbol, asOf, ct);
            if (snap.EarningsGrowthAvailability != DataAvailability.Unknown || snap.RevenueGrowthAvailability != DataAvailability.Unknown)
            {
                f = snap;
                // The snapshot before this one (latest on/before the day prior to the current snapshot's date), if one exists.
                var earlier = await fundamentals.GetFundamentalsAsync(symbol, snap.AsOfDate.AddDays(-1), ct);
                if (earlier.AsOfDate < snap.AsOfDate && (earlier.EarningsGrowthAvailability != DataAvailability.Unknown || earlier.RevenueGrowthAvailability != DataAvailability.Unknown)) prior = earlier;
            }
        }
        if (institutional is not null)
        {
            var snap = await institutional.GetActivityAsync(symbol, asOf, ct);
            if (snap.Availability != DataAvailability.Unknown) i = snap;
        }
        return (f, i, prior);
    }
}

/// <summary>
/// IScanRunner for ScannerBackgroundService (spec §7.1). A cycle that cannot run (stale/empty cache) throws,
/// which ScannerBackgroundService already catches and logs per-cycle without killing the service — so a missing
/// cache surfaces as a visible error every interval instead of a silently-empty "successful" scan.
/// </summary>
public sealed class ScanRunner : IScanRunner
{
    private readonly IInstrumentRepository _instruments;
    private readonly IHistoricalBarRepository _bars;
    private readonly IUniverseClassificationRepository _classifications;
    private readonly ISurveillanceRepository _surveillance;
    private readonly IScannerResultRepository _results;
    private readonly ScannerEngineOptions _engineOptions;
    private readonly BenchmarkOptions _benchmarks;
    private readonly ILogger _logger;
    private readonly INotificationService? _notifier;
    private readonly IFundamentalDataProvider? _fundamentals;
    private readonly IInstitutionalActivityProvider? _institutional;
    private readonly IMarketRegimeRepository? _regimes;
    private readonly ITradeRepository? _trades;
    private readonly IScanEvidenceRepository? _evidence;
    private readonly IDeliveryDataRepository? _delivery;

    public ScanRunner(IInstrumentRepository instruments, IHistoricalBarRepository bars, IUniverseClassificationRepository classifications,
        ISurveillanceRepository surveillance, IScannerResultRepository results, ScannerEngineOptions engineOptions,
        BenchmarkOptions benchmarks, ILogger logger, INotificationService? notifier = null,
        IFundamentalDataProvider? fundamentals = null, IInstitutionalActivityProvider? institutional = null,
        IMarketRegimeRepository? regimes = null, ITradeRepository? trades = null, IScanEvidenceRepository? evidence = null, IDeliveryDataRepository? delivery = null)
    {
        _delivery = delivery; _fundamentals = fundamentals; _institutional = institutional; _regimes = regimes; _trades = trades; _evidence = evidence;
        _instruments = instruments; _bars = bars; _classifications = classifications; _surveillance = surveillance;
        _results = results; _engineOptions = engineOptions; _benchmarks = benchmarks; _logger = logger; _notifier = notifier;
    }

    public async Task<IReadOnlyList<ScannerResult>> RunScanAsync(CancellationToken cancellationToken)
    {
        var outcome = await ScanCycle.RunAsync(_instruments, _bars, _classifications, _surveillance, _results, _engineOptions, _benchmarks, _logger, cancellationToken, _fundamentals, _institutional, _regimes, _trades, _evidence, _delivery);
        if (outcome.Error is not null)
        {
            if (_notifier is not null) await _notifier.NotifyAsync(NotificationCategories.SystemFailure, $"Scheduled scan could not run: {outcome.Error}", cancellationToken);
            throw new InvalidOperationException(outcome.Error);
        }
        _logger.LogInformation("scheduled scan: {Scanned} scanned, {Ranked} ranked as of {AsOf} in {Ms}ms.", outcome.LoadedCount, outcome.Ranked.Count, outcome.AsOfDate, (long)outcome.Elapsed.TotalMilliseconds);
        return outcome.Ranked;
    }
}
