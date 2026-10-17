using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// Shared "load the active universe + benchmark bars from local SQLite"
/// logic for `scan` and `backtest` (Tranche 18) — both need essentially
/// the same instrument/bars/classification/surveillance assembly, so it
/// lives here once rather than duplicated per command.
/// </summary>
public static class UniverseDataLoader
{
    public sealed record LoadedSymbol(
        Instrument Instrument,
        IReadOnlyList<HistoricalBar> Bars,
        UniverseClassificationSnapshot? Classification,
        SurveillanceStatus Surveillance)
    {
        /// <summary>Unknown, not a fabricated Large/Mid guess, when no classification snapshot exists (spec §2.2).</summary>
        public MarketCapBucket MarketCapBucket => Classification?.MarketCapBucket ?? MarketCapBucket.Unknown;
    }

    /// <summary>
    /// Every active Instrument that has at least one cached bar in
    /// [barsFrom, barsTo]. Symbols with zero cached bars are skipped with a
    /// warning (not an error — a partially-populated cache is normal
    /// during development) rather than failing the whole run. Point-in-time
    /// classification/surveillance are both resolved as of `asOfDate` — for
    /// `scan` that's "today"; for `backtest` it's necessarily today's data
    /// too (spec §5.6 SurvivorshipBiased flag exists precisely because this
    /// codebase has no genuine point-in-time constituent/surveillance
    /// history ingestion yet — see BacktestCommand).
    /// </summary>
    public static async Task<IReadOnlyList<LoadedSymbol>> LoadActiveUniverseAsync(
        IInstrumentRepository instruments, IHistoricalBarRepository bars, IUniverseClassificationRepository classifications,
        ISurveillanceRepository surveillance, DateOnly barsFrom, DateOnly barsTo, DateOnly asOfDate,
        ILogger logger, CancellationToken cancellationToken = default, int surveillanceMaxAgeDays = 0)
    {
        var active = await instruments.GetActiveAsync(cancellationToken);
        var result = new List<LoadedSymbol>(active.Count);

        foreach (var instrument in active)
        {
            var symbolBars = await bars.GetRangeAsync(instrument.Symbol, barsFrom, barsTo, Timeframe.Daily, cancellationToken);
            if (symbolBars.Count == 0)
            {
                logger.LogWarning(
                    "No cached bars for {Symbol} in {From}..{To} — skipped. Run 'dotnet run -- fetch --symbol {Symbol} --from {From} --to {To}' to cache it.",
                    instrument.Symbol, barsFrom.ToString("yyyy-MM-dd"), barsTo.ToString("yyyy-MM-dd"), instrument.Symbol, barsFrom.ToString("yyyy-MM-dd"), barsTo.ToString("yyyy-MM-dd"));
                // dates are pre-formatted: a DateOnly passed to the logger is rendered in the machine's culture (e.g. 06/03/2025), which `fetch` rejects
                continue;
            }

            var classification = await classifications.GetClassificationAsync(instrument.Symbol, asOfDate, cancellationToken);
            // maxAge 0 = exact date (backtests: point-in-time, never borrow a status from another day). Scan/paper/live pass
            // Scanner:SurveillanceMaxStalenessDays so Monday can use Friday's published list — but nothing older than that window.
            var status = surveillanceMaxAgeDays > 0
                ? await surveillance.GetLatestStatusAsync(instrument.Symbol, asOfDate, surveillanceMaxAgeDays, cancellationToken)
                : await surveillance.GetStatusAsync(instrument.Symbol, asOfDate, cancellationToken);

            result.Add(new LoadedSymbol(instrument, symbolBars, classification, status));
        }

        return result;
    }

    /// <summary>Broad (required by every downstream engine) and midcap (optional — every consumer already renormalizes when it's absent, Tranche 5) index bars, keyed by BenchmarkOptions' configured symbols.</summary>
    public static async Task<(IReadOnlyList<HistoricalBar> Broad, IReadOnlyList<HistoricalBar>? Midcap)> LoadBenchmarksAsync(
        IHistoricalBarRepository bars, BenchmarkOptions benchmarkOptions, DateOnly from, DateOnly to,
        ILogger logger, CancellationToken cancellationToken = default)
    {
        var broad = await bars.GetRangeAsync(benchmarkOptions.BroadIndexSymbol, from, to, Timeframe.Daily, cancellationToken);
        if (broad.Count == 0)
        {
            logger.LogWarning(
                "No cached bars for the broad-index benchmark {Symbol} — regime/relative-strength calculations will degrade sharply. Run 'dotnet run -- fetch --symbol {Symbol} --from {From} --to {To}' first.",
                benchmarkOptions.BroadIndexSymbol, benchmarkOptions.BroadIndexSymbol, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
        }

        IReadOnlyList<HistoricalBar>? midcap = null;
        if (benchmarkOptions.MidcapIndexSymbol is { Length: > 0 } midcapSymbol)
        {
            var midcapBars = await bars.GetRangeAsync(midcapSymbol, from, to, Timeframe.Daily, cancellationToken);
            if (midcapBars.Count > 0) midcap = midcapBars;
            else logger.LogInformation("No cached bars for the midcap-index benchmark {Symbol} — RS blending renormalizes across the remaining benchmarks instead (Tranche 5).", midcapSymbol);
        }

        return (broad, midcap);
    }

    /// <summary>
    /// Resolved simplification (documented on BacktestSymbolContext itself,
    /// Tranche 11): the engine only accepts one static flag for the WHOLE
    /// run, not genuine point-in-time surveillance history. Availability ==
    /// Unknown (no surveillance record at all — the overwhelming common
    /// case, since no CSV/ingestion pipeline exists yet) resolves to FALSE
    /// here deliberately: treating "we never ingested surveillance data"
    /// as "critically flagged" would make the microcap gate (spec §2.3/§8
    /// — Tranche 8) reject every single microcap candidate outright,
    /// which would silently make the feature look broken rather than
    /// silently make it look permissive. Genuine absence of surveillance
    /// data is a documented gap (see docs/gap-log.md), not something this
    /// method should paper over by fabricating a worst-case flag.
    /// </summary>
    public static bool HasCriticalSurveillanceFlag(SurveillanceStatus status) =>
        status.Availability == DataAvailability.Available &&
        (status.Flags & (RiskFlags.Asm | RiskFlags.Gsm | RiskFlags.TradeToTrade | RiskFlags.SurveillanceFlag)) != RiskFlags.None;

    public static bool HasSevereDataQualityIssue(SurveillanceStatus status) =>
        status.Availability == DataAvailability.Available && (status.Flags & RiskFlags.DataQualityRisk) != RiskFlags.None;
}
