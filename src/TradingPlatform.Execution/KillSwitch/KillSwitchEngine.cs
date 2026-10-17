using TradingPlatform.Core.Enums;

namespace TradingPlatform.Execution.KillSwitch;

/// <summary>
/// Kill-switch state (spec §6.4): "When active: no new orders (optionally
/// allow risk-reducing exits only)." A latch, threaded call-to-call the
/// same explicit-state-in/out way as DrawdownState (Tranche 10) — engaging
/// requires an explicit source; clearing requires an explicit,
/// caller-acknowledged reset (never auto-clears just because the
/// triggering condition went away, mirroring DrawdownEngine's own
/// manual-reset philosophy for the same reason: momentary recovery is not
/// evidence the underlying problem was actually addressed).
/// </summary>
public sealed record KillSwitchState
{
    public required bool IsActive { get; init; }
    public KillSwitchSource? Source { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset? ActivatedAt { get; init; }

    /// <summary>Whether a risk-reducing (Sell) order should still be allowed while active — spec §6.4's explicit carve-out. False for sources where even exits aren't trustworthy (e.g. DataError — a broker/data problem can make even "sell everything" dangerous if the data driving it is the thing that's wrong).</summary>
    public bool AllowsRiskReducingExits { get; init; }

    public static KillSwitchState Inactive => new() { IsActive = false };
}

/// <summary>
/// Activates/clears the kill switch (spec §6.4). Pure state transitions —
/// the Order Manager is responsible for actually deciding WHEN to call
/// Activate (e.g. wiring DrawdownState.IsTradingShutdown or
/// PortfolioRiskGateResult.BlocksNewEntries to KillSwitchSource.Drawdown/
/// DailyLossLimit) and for enforcing the resulting state against every
/// order it submits.
/// </summary>
public static class KillSwitchEngine
{
    /// <summary>
    /// Sources where a data or connectivity problem is itself the trigger
    /// get NO exit carve-out — spec §6.4 says exits "may optionally" stay
    /// allowed, and the one case where allowing them is clearly wrong is
    /// when the very thing that's broken is trust in the data/connection
    /// that a sell order would rely on. Every other source (risk-driven or
    /// operational) still permits risk-reducing exits.
    /// </summary>
    private static readonly HashSet<KillSwitchSource> NoExitCarveOutSources = new()
    {
        KillSwitchSource.DataError,
        KillSwitchSource.ConnectivityFailure,
    };

    public static KillSwitchState Activate(KillSwitchState previous, KillSwitchSource source, string reason, DateTimeOffset activatedAt)
    {
        // Already active: keep the ORIGINAL trigger (first cause wins) — a second, different
        // source firing while already halted doesn't overwrite what actually caused the halt,
        // which matters for the human who has to investigate and manually clear it.
        if (previous.IsActive) return previous;

        return new KillSwitchState
        {
            IsActive = true,
            Source = source,
            Reason = reason,
            ActivatedAt = activatedAt,
            AllowsRiskReducingExits = !NoExitCarveOutSources.Contains(source),
        };
    }

    /// <summary>Explicit, caller-acknowledged clear — never automatic (see class remarks).</summary>
    public static KillSwitchState Reset() => KillSwitchState.Inactive;

    /// <summary>Whether an order of the given side may proceed given the current kill-switch state.</summary>
    public static bool AllowsOrder(KillSwitchState state, OrderSide side)
    {
        if (!state.IsActive) return true;
        return side == OrderSide.Sell && state.AllowsRiskReducingExits;
    }
}
