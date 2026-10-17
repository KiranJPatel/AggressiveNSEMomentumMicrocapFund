using TradingPlatform.Core.Models;
using TradingPlatform.Risk.Correlation;

namespace TradingPlatform.Scanner;

/// <summary>Everything ScannerEngine needs for one candidate symbol.</summary>
public sealed class ScannerSymbolContext
{
    public required Instrument Instrument { get; init; }
    public required IReadOnlyList<HistoricalBar> Bars { get; init; }
    public IReadOnlyList<HistoricalBar>? SectorBenchmarkBars { get; init; }
    public FundamentalSnapshot? Fundamentals { get; init; }
    /// <summary>The snapshot BEFORE <see cref="Fundamentals"/> (null when there is only one), so growth acceleration can be scored.</summary>
    public FundamentalSnapshot? PriorFundamentals { get; init; }
    public InstitutionalActivitySnapshot? InstitutionalActivity { get; init; }

    /// <summary>Imported NSE delivery records for this symbol (any order); null/empty when none were imported (spec §3.4).</summary>
    public IReadOnlyList<DeliveryRecord>? DeliveryRecords { get; init; }

    /// <summary>
    /// Session whose delivery figure is used. Null = the date of the last bar. NSE publishes delivery only after the close, so a scan over a
    /// real-time overlay bar (today, still forming) sets this to the last COMPLETED session — otherwise a delivery gate could never pass intraday.
    /// </summary>
    public DateOnly? DeliveryAsOfDate { get; init; }
    public required SurveillanceStatus Surveillance { get; init; }
    public UniverseClassificationSnapshot? Classification { get; init; }
}

/// <summary>
/// Optional — when supplied, ScannerEngine computes real
/// SuggestedEntry/SuggestedStop/Risk/SuggestedPositionSize via
/// InitialStopCalculator/PositionSizingCalculator (Tranches 8/9), the same
/// engines BacktestEngine uses (Tranche 11); when omitted, those four
/// columns are left at their type defaults (0) rather than a fabricated
/// placeholder — a scan can still usefully rank candidates by Score alone
/// without a live portfolio snapshot to size against.
/// </summary>
public sealed class ScannerPortfolioContext
{
    public required decimal Nav { get; init; }
    public required decimal CashBalance { get; init; }

    /// <summary>Only the drawdown/loss-limit axis is expected here — ScannerEngine itself computes the regime axis fresh (it already calculates MarketRegimeSnapshot for BreakoutStatus) and takes the MIN itself, the same two-axis combination BacktestEngine performs (Tranche 11/10).</summary>
    public required decimal DeployableCapitalPercentFromDrawdownAxis { get; init; }

    public required decimal CurrentPortfolioHeatValue { get; init; }
    public required decimal CurrentMicrocapExposureValue { get; init; }
    public required IReadOnlyDictionary<string, decimal> CurrentSectorExposureValueBySector { get; init; }

    /// <summary>Existing open positions' own bars/values, for CorrelationClusterCalculator (Tranche 8) — correlation is inherently per-candidate, so this can't be a single pre-aggregated number the way sector/microcap/heat are.</summary>
    public required IReadOnlyList<CorrelatedPosition> ExistingOpenPositionsForCorrelation { get; init; }
}
