using TradingPlatform.Core.Configuration;

namespace TradingPlatform.Risk.Drawdown;

/// <summary>
/// Running drawdown-tracking state (spec §4.5: "Track: Peak NAV, Current
/// NAV, Drawdown %, Max Drawdown, Drawdown Duration, Recovery Duration").
///
/// Threaded call-to-call by the caller — DrawdownEngine is a pure function
/// of (previous state, new observation), the same explicit-state-in/out
/// shape as TrailingStopCalculator (Tranche 9) and StagedPositionState
/// (Tranche 7). This engine has no clock and no I/O of its own: whoever
/// owns the actual persistence of this state across a process restart
/// (Backtesting orchestration now, live restart-recovery in Tranche 15)
/// is responsible for saving/reloading it — deliberately out of scope
/// here, the same layering choice already made for PositionSizingInputs'
/// "current exposure" fields (Tranche 8).
///
/// "Duration" fields are counted in units of whatever the caller's AsOfDate
/// cadence is (this engine assumes one observation per trading day, but
/// never assumes anything about calendar gaps beyond subtracting dates) —
/// i.e. calendar days between the tracked AsOfDate values, not necessarily
/// trading days, since this engine is never told which dates are holidays.
/// </summary>
public sealed record DrawdownState
{
    public required DateOnly AsOfDate { get; init; }
    public required decimal CurrentNav { get; init; }

    public required decimal PeakNav { get; init; }
    public required DateOnly PeakDate { get; init; }

    /// <summary>0 when CurrentNav is at/above PeakNav.</summary>
    public required decimal DrawdownPercent { get; init; }

    /// <summary>First date this drawdown episode went underwater (cleared back to null once a new peak is made). Null while at a peak.</summary>
    public DateOnly? DrawdownStartDate { get; init; }

    /// <summary>Days since DrawdownStartDate, while still underwater; null at a peak.</summary>
    public int? DrawdownDurationDays { get; init; }

    /// <summary>The date of this episode's deepest point so far (updates only while the episode is still deepening). Null outside an episode.</summary>
    public DateOnly? TroughDate { get; init; }

    /// <summary>DrawdownPercent at TroughDate — the deepest point reached so far within the CURRENT episode specifically (may be less than the all-time MaxDrawdownPercent, if a worse episode happened previously). Null outside an episode.</summary>
    public decimal? EpisodeTroughDrawdownPercent { get; init; }

    /// <summary>Days from TroughDate to the date this episode recovered back to a new peak. Populated only on the observation that makes the new peak; carries the LAST completed episode's figure otherwise (never recomputed once set), so a caller can always see "how long did the last recovery take".</summary>
    public int? LastRecoveryDurationDays { get; init; }

    /// <summary>All-time high-water mark of DrawdownPercent — never decreases, even after recovery (spec §4.5 "Max Drawdown").</summary>
    public required decimal MaxDrawdownPercent { get; init; }
    public DateOnly? MaxDrawdownDate { get; init; }

    public required DrawdownTier CurrentTier { get; init; }

    /// <summary>
    /// Latches true the first time CurrentTier.IsShutdown is reached and
    /// stays true — even once NAV fully recovers — until ManualReset is
    /// explicitly acknowledged via DrawdownEngine.Evaluate's
    /// manualResetAcknowledged parameter (spec §4.5: "40% TRADING SHUTDOWN
    /// — no new trades, explicit manual reset required"). This latch, not
    /// CurrentTier alone, is what a caller must check to decide whether
    /// trading is halted.
    /// </summary>
    public required bool RequiresManualReset { get; init; }

    /// <summary>True whenever trading must not open new positions: either the latch above is set, or the currently-computed tier is itself the shutdown tier.</summary>
    public bool IsTradingShutdown => RequiresManualReset || CurrentTier.IsShutdown;
}
