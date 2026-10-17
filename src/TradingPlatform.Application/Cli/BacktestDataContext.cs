using Microsoft.Extensions.Logging;
using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Cli;

/// <summary>
/// Universe/benchmark bars loaded once and reused to build many
/// BacktestRunRequests over different sub-ranges — exactly what
/// `walkforward` (one request per Train/Validate/Test sub-period per
/// window) and `optimize` (one request per Train/Validate split per grid
/// combination) both need, without re-hitting SQLite for the same data
/// dozens of times. `backtest` (Tranche 18) doesn't use this — it only
/// ever needs one request, so its own inline loading was simpler to leave
/// as-is rather than retrofit onto this once it already worked.
/// </summary>
public sealed class BacktestDataContext
{
    public required IReadOnlyList<HistoricalBar> BroadIndexBars { get; init; }
    public IReadOnlyList<HistoricalBar>? MidcapIndexBars { get; init; }
    public required IReadOnlyList<BacktestSymbolContext> Symbols { get; init; }
    public required string DataVersion { get; init; }

    public BacktestRunRequest BuildRequest(DateOnly start, DateOnly end, string strategyVersion, string configurationHash) => new()
    {
        StrategyVersion = strategyVersion,
        ConfigurationHash = configurationHash,
        DataVersion = DataVersion,
        StartDate = start,
        EndDate = end,
        // Same resolved simplification as `backtest` (Tranche 18) — see
        // UniverseDataLoader's remarks: no genuine point-in-time
        // constituent/surveillance history is ingested by anything in
        // this codebase yet.
        SurvivorshipBiased = true,
        BroadIndexBars = BroadIndexBars,
        MidcapIndexBars = MidcapIndexBars,
        Symbols = Symbols,
    };

    public static async Task<BacktestDataContext?> LoadAsync(
        IInstrumentRepository instruments, IHistoricalBarRepository bars, IUniverseClassificationRepository classifications,
        ISurveillanceRepository surveillance, BenchmarkOptions benchmarkOptions, DateOnly overallFrom, DateOnly overallTo,
        ILogger logger, CancellationToken cancellationToken = default, TradingPlatform.Core.Enums.SignalToOrderLag refinementMode = TradingPlatform.Core.Enums.SignalToOrderLag.NextOpen,
        IDeliveryDataRepository? deliveries = null, StockScoringOptions? scoring = null, StrategyOptions? strategy = null)
    {
        const int warmupCalendarDays = 500;
        var barsFrom = overallFrom.AddDays(-warmupCalendarDays);

        var (broadBars, midcapBars) = await UniverseDataLoader.LoadBenchmarksAsync(bars, benchmarkOptions, barsFrom, overallTo, logger, cancellationToken);
        if (broadBars.Count == 0) return null;

        var loaded = await UniverseDataLoader.LoadActiveUniverseAsync(instruments, bars, classifications, surveillance, barsFrom, overallTo, overallTo, logger, cancellationToken);
        if (loaded.Count == 0) return null;

        var symbolContexts = loaded.Select(s => new BacktestSymbolContext
        {
            Symbol = s.Instrument.Symbol,
            Bars = s.Bars,
            MarketCapBucket = s.MarketCapBucket,
            Sector = s.Classification?.Sector ?? s.Instrument.Sector,
            HasCriticalSurveillanceFlag = UniverseDataLoader.HasCriticalSurveillanceFlag(s.Surveillance),
            HasSevereDataQualityIssue = UniverseDataLoader.HasSevereDataQualityIssue(s.Surveillance),
        }).ToList();

        if (refinementMode != TradingPlatform.Core.Enums.SignalToOrderLag.NextOpen)
            symbolContexts = (await IntradayContextLoader.AttachAsync(symbolContexts, bars, refinementMode, overallFrom, overallTo, logger, cancellationToken)).Symbols.ToList();

        if (deliveries is not null && scoring is not null && strategy is not null)
            symbolContexts = (await DeliveryContextLoader.AttachToBacktestAsync(symbolContexts, deliveries, scoring, strategy, overallFrom, overallTo, logger, cancellationToken)).ToList();

        var latestBarDate = loaded.SelectMany(s => s.Bars).Select(b => b.Timestamp).Concat(broadBars.Select(b => b.Timestamp)).Max();
        var totalBarCount = loaded.Sum(s => s.Bars.Count) + broadBars.Count + (midcapBars?.Count ?? 0);

        return new BacktestDataContext
        {
            BroadIndexBars = broadBars,
            MidcapIndexBars = midcapBars,
            Symbols = symbolContexts,
            DataVersion = $"sqlite:{totalBarCount}bars:latest={latestBarDate:yyyy-MM-dd}",
        };
    }
}
