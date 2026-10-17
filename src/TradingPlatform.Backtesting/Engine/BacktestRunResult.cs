using TradingPlatform.Backtesting.Execution;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Backtesting.Engine;

/// <summary>
/// Output of one BacktestEngine.Run. Trades/TradeLegs only ever contain
/// FULLY CLOSED positions (spec §7.2's Trade record requires an
/// ExitPrice/ExitReason/ExitAt) — any position still open at EndDate
/// contributes its unrealized value to the final day's DailySnapshots.Nav
/// (and so to Run.FinalCapital) but is deliberately never force-closed
/// into a synthetic Trade the strategy never actually decided to exit.
/// </summary>
public sealed class BacktestRunResult
{
    public required BacktestRun Run { get; init; }
    public required IReadOnlyList<Trade> Trades { get; init; }
    public required IReadOnlyList<TradeLeg> TradeLegs { get; init; }
    public required IReadOnlyList<PortfolioSnapshot> DailySnapshots { get; init; }
    public required IReadOnlyList<DrawdownEvent> DrawdownEvents { get; init; }

    /// <summary>How entries were refined (null when SignalToOrderLag is NextOpen). Not persisted; printed by the backtest command.</summary>
    public RefinementStats? Refinement { get; init; }

    /// <summary>Pyramid add-on orders cancelled because the position they would have added to had already been exited by the time they filled.</summary>
    public int OrphanedAddOnOrdersCancelled { get; init; }

    /// <summary>How far above/below the planned (pivot) entry Stage-1 entries actually filled, and what the gap-risk guard did about it. Null when no Stage-1 entry was attempted.</summary>
    public EntryExecutionStats? EntryExecution { get; init; }
}

/// <summary>Stage-1 entry execution versus the planned entry: <see cref="MeanFillVsPlannedEntryPercent"/> is the average (open / planned entry - 1) in percent.</summary>
public sealed record EntryExecutionStats(int Entries, decimal MeanFillVsPlannedEntryPercent, int Resized, int Cancelled, bool GuardEnabled);
