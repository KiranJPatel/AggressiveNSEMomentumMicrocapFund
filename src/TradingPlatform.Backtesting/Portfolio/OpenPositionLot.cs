using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Backtesting.Portfolio;

/// <summary>
/// One open position as tracked DURING a backtest run — richer than
/// Core.Models.Position (which is the persisted/reported shape) because
/// the simulator needs a few extra running figures purely to eventually
/// produce a correct Trade record on close: HighestPriceSinceEntry/
/// LowestPriceSinceEntry (for MFE/MAE), TotalCostBasisIncludingFees (so
/// realized P&amp;L nets out every fee paid across every staged fill, not
/// just the raw price difference), and Legs (so the eventual Trade gets
/// its full TradeLeg audit trail — spec §7.2/§3.7).
///
/// InitialStop/RiskPerShare/InitialRiskAmount are fixed at the Stage 1
/// fill and never change as Stage 2/3 add shares — spec §3.7's staged
/// model sizes the FULL target position once, up front (see
/// StagedEntryCalculator, Tranche 7), and later stages just fill
/// predetermined share counts at whatever price they actually trade at.
/// "1R" for this trade is therefore pinned to the original entry
/// decision, exactly like every other R-multiple in this codebase
/// (TrailingActiveTriggerR, etc.) — never redefined as the average cost
/// basis drifts with Stage 2/3 fills.
/// </summary>
public sealed class OpenPositionLot
{
    public required string Symbol { get; init; }

    public decimal Quantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal TotalCostBasisIncludingFees { get; set; }

    public required decimal InitialStop { get; init; }
    public decimal CurrentStop { get; set; }
    public required decimal RiskPerShare { get; init; }
    public required decimal InitialRiskAmount { get; init; }

    public PyramidStage HighestStageFilled { get; set; }
    public required DateTimeOffset OpenedAt { get; init; }

    public decimal HighestPriceSinceEntry { get; set; }
    public decimal LowestPriceSinceEntry { get; set; }

    /// <summary>
    /// The FULL target quantity sized once at Stage 1 (spec §3.7's staged
    /// model plans the whole position up front — see StagedEntryCalculator,
    /// Tranche 7) — Stage 2/3 fills draw predetermined quantities against
    /// this same total rather than being independently re-sized.
    /// </summary>
    public decimal TotalTargetQuantity { get; set; }

    /// <summary>Needed (alongside Stage2EntryPrice/Timestamp below) because Stage2Trigger/Stage3Trigger evaluators measure "+2-3% favorable move" and "retest" from each STAGE's own entry, never the blended AverageEntryPrice (see StagedPositionState's XML doc, Tranche 7).</summary>
    public decimal Stage1EntryPrice { get; set; }
    public DateTimeOffset Stage1EntryTimestamp { get; set; }
    public decimal? Stage2EntryPrice { get; set; }
    public DateTimeOffset? Stage2EntryTimestamp { get; set; }

    public long? SignalId { get; init; }
    public int RegimeScoreAtEntry { get; init; }
    public decimal StockScoreAtEntry { get; init; }
    public decimal RiskAtEntryPercent { get; init; }
    public required string StrategyVersion { get; init; }
    public required string ConfigurationVersion { get; init; }

    public List<TradeLeg> Legs { get; } = new();

    /// <summary>
    /// Running total of realized P&amp;L from any PARTIAL exit fills so far
    /// (spec §5.1/§5.4 explicitly require partial-fill support, including
    /// on the exit side — a liquidity-capped exit on a large position can
    /// take more than one day to fully unwind). Zero until the first
    /// partial exit; folded into the final Trade's RealizedPnl once the
    /// position fully closes.
    /// </summary>
    public decimal RealizedPnlFromPartialExits { get; set; }

    /// <summary>Every exit fill so far, partial or final — used to compute the closed Trade's weighted-average ExitPrice and to attribute the final ExitReason to whichever slice actually finished the position off.</summary>
    public List<ExitFillRecord> ExitFills { get; } = new();
}

/// <summary>One exit fill (partial or the final, fully-closing slice) against an OpenPositionLot.</summary>
public sealed record ExitFillRecord(decimal Quantity, decimal Price, DateTimeOffset FilledAt, ExitReason Reason);
