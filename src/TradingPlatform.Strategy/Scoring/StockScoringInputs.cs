using TradingPlatform.Core.Models;

namespace TradingPlatform.Strategy.Scoring;

/// <summary>
/// Inputs to StockScoringEngine. RelativeStrengthScore and
/// SectorStrengthScore are accepted pre-computed rather than derived here:
/// both come from Tranche 5's RelativeStrengthEngine/Calculator, and
/// SectorStrengthScore in particular (sector-benchmark-vs-broad-index RS)
/// is identical for every stock in the same sector on a given day — it
/// belongs computed once by the caller/orchestrator and passed to every
/// stock's scoring call, not recomputed per-stock here.
/// </summary>
public sealed class StockScoringInputs
{
    public required string Symbol { get; init; }
    public required DateOnly AsOfDate { get; init; }

    /// <summary>Ascending, the stock's own OHLCV.</summary>
    public required IReadOnlyList<HistoricalBar> Bars { get; init; }

    /// <summary>Ascending, index-aligned with Bars.</summary>
    public required IReadOnlyList<IndicatorSnapshot> IndicatorSnapshots { get; init; }

    /// <summary>Null if no VCP is currently forming — a genuine assessment, not missing data (see StockScoringEngine).</summary>
    public VcpPattern? VcpPattern { get; init; }

    public required decimal RelativeStrengthScore { get; init; }
    public decimal? SectorStrengthScore { get; init; }

    public FundamentalSnapshot? CurrentFundamentals { get; init; }
    public FundamentalSnapshot? PriorFundamentals { get; init; }
    public InstitutionalActivitySnapshot? InstitutionalActivity { get; init; }

    /// <summary>Delivery % vs the stock's own recent norm (spec §3.4). Null when no delivery data exists for the as-of date or too little history.</summary>
    public DeliverySnapshot? Delivery { get; init; }
}
