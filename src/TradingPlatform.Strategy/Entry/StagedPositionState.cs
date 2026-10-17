namespace TradingPlatform.Strategy.Entry;

using TradingPlatform.Core.Enums;

/// <summary>
/// The subset of an open position's state the staged-entry triggers need.
/// Deliberately NOT Core.Models.Position: that model stores a single
/// blended AverageEntryPrice (needed for portfolio/P&L accounting), but
/// Stage 2/3 triggers need each STAGE's own entry price and fill
/// timestamp specifically (spec §3.7's "+2-3% favorable move" and "retest"
/// are both measured from the most recent stage's own entry, not the
/// blended average). Reconstructing this from persisted TradeLegs is an
/// orchestration concern for whichever tranche wires persistence to live
/// strategy execution (Execution, later) — this type keeps that wiring
/// decision out of the pure decision logic here.
/// </summary>
public sealed class StagedPositionState
{
    public required string Symbol { get; init; }
    public required PyramidStage HighestStageFilled { get; init; }
    public required decimal CurrentStop { get; init; }

    public required decimal Stage1EntryPrice { get; init; }
    public required DateTimeOffset Stage1EntryTimestamp { get; init; }

    public decimal? Stage2EntryPrice { get; init; }
    public DateTimeOffset? Stage2EntryTimestamp { get; init; }
}
