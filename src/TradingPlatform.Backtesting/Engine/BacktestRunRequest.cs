using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Backtesting.Engine;

/// <summary>
/// Everything BacktestEngine needs for one symbol. Bars should include
/// warm-up history before RunRequest.StartDate (indicators/VCP/RS all need
/// lookback) — the engine only ever ACTS on dates within
/// [StartDate, EndDate], never before, but reads earlier bars freely for
/// warm-up the same way every other no-look-ahead calculator in this
/// codebase does (bars up to and including "today", nothing after).
///
/// Sector/fundamentals/institutional-activity are all optional, mirroring
/// spec §2.2/§3.2's "Unknown/Unavailable is a legitimate state, never
/// silently defaulted" pattern already established for every other
/// engine — StockScoringEngine and PositionSizingCalculator both already
/// know how to treat these as absent rather than fabricating a neutral
/// value.
/// </summary>
public sealed class BacktestSymbolContext
{
    public required string Symbol { get; init; }
    public required IReadOnlyList<HistoricalBar> Bars { get; init; }
    public required MarketCapBucket MarketCapBucket { get; init; }
    public string? Sector { get; init; }
    public IReadOnlyList<HistoricalBar>? SectorBenchmarkBars { get; init; }

    /// <summary>Point-in-time; the engine looks up the exact AsOfDate only — no "most recent prior" fallback (caller pre-resolves that if desired).</summary>
    public IReadOnlyDictionary<DateOnly, FundamentalSnapshot>? FundamentalsByDate { get; init; }
    public IReadOnlyDictionary<DateOnly, InstitutionalActivitySnapshot>? InstitutionalActivityByDate { get; init; }

    /// <summary>
    /// Simplification, documented: static for the whole run rather than a
    /// genuine point-in-time series — real point-in-time surveillance
    /// ingestion is Scanner/Universe territory (later tranches). A symbol
    /// known to carry a critical flag for the WHOLE backtest period can
    /// still be modeled today by setting this true; per-date surveillance
    /// history is future work.
    /// </summary>
    public bool HasCriticalSurveillanceFlag { get; init; }
    public bool HasSevereDataQualityIssue { get; init; }

    /// <summary>Optional 60m bars (spec §7.3 entry refinement). Needed only when Backtest.SignalToOrderLag is Refinement60m/15m; sessions without them follow RefinementSkipWhenNoIntradayData.</summary>
    public IReadOnlyList<HistoricalBar>? HourlyBars { get; init; }

    /// <summary>Optional 15m bars (spec §7.3 execution layer), used only by Refinement15m.</summary>
    public IReadOnlyList<HistoricalBar>? QuarterHourBars { get; init; }

    /// <summary>Imported NSE delivery records (spec §3.4); null/empty = none. Used only when scoring or the breakout gate has delivery turned on.</summary>
    public IReadOnlyList<DeliveryRecord>? DeliveryRecords { get; init; }

    /// <summary>Copy of this context carrying delivery records.</summary>
    public BacktestSymbolContext WithDelivery(IReadOnlyList<DeliveryRecord>? records) => new()
    {
        Symbol = Symbol, Bars = Bars, MarketCapBucket = MarketCapBucket, Sector = Sector, SectorBenchmarkBars = SectorBenchmarkBars,
        FundamentalsByDate = FundamentalsByDate, InstitutionalActivityByDate = InstitutionalActivityByDate,
        HasCriticalSurveillanceFlag = HasCriticalSurveillanceFlag, HasSevereDataQualityIssue = HasSevereDataQualityIssue,
        HourlyBars = HourlyBars, QuarterHourBars = QuarterHourBars, DeliveryRecords = records,
    };

    /// <summary>Copy of this context with intraday bars attached (the type is init-only, so callers cannot just set them).</summary>
    public BacktestSymbolContext WithIntraday(IReadOnlyList<HistoricalBar>? hourly, IReadOnlyList<HistoricalBar>? quarterHour) => new()
    {
        Symbol = Symbol, Bars = Bars, MarketCapBucket = MarketCapBucket, Sector = Sector, SectorBenchmarkBars = SectorBenchmarkBars,
        FundamentalsByDate = FundamentalsByDate, InstitutionalActivityByDate = InstitutionalActivityByDate,
        HasCriticalSurveillanceFlag = HasCriticalSurveillanceFlag, HasSevereDataQualityIssue = HasSevereDataQualityIssue,
        HourlyBars = hourly, QuarterHourBars = quarterHour, DeliveryRecords = DeliveryRecords,
    };
}

/// <summary>Top-level request to BacktestEngine.Run (spec §5.1).</summary>
public sealed class BacktestRunRequest
{
    public required string StrategyVersion { get; init; }
    public required string ConfigurationHash { get; init; }
    public required string DataVersion { get; init; }
    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }

    /// <summary>
    /// Required, not defaulted — spec §5.6 treats this as a mandatory,
    /// non-optional field on the report itself. The caller must
    /// affirmatively state whether Symbols represents a genuine
    /// point-in-time historical universe or today's constituent list
    /// applied retroactively; there is no safe engine-computed default.
    /// </summary>
    public required bool SurvivorshipBiased { get; init; }

    public required IReadOnlyList<HistoricalBar> BroadIndexBars { get; init; }
    public IReadOnlyList<HistoricalBar>? MidcapIndexBars { get; init; }

    public required IReadOnlyList<BacktestSymbolContext> Symbols { get; init; }
}
