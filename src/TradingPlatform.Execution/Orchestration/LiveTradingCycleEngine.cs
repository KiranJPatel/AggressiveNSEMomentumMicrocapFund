using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.KillSwitch;
using TradingPlatform.Execution.OrderManagement;
using TradingPlatform.Execution.Reconciliation;
using TradingPlatform.Execution.Validation;
using TradingPlatform.Risk.Drawdown;

namespace TradingPlatform.Execution.Orchestration;

/// <summary>
/// Everything one live-trading cycle needs. ExitIntents/EntryIntents are
/// pre-decided by the caller (ExitEngine/TrailingStopCalculator for
/// exits, Tranche 9; ScannerEngine for entries, Tranche 14) — this engine
/// doesn't evaluate trading decisions itself, only the SAFETY GATING
/// around submitting whatever was already decided: reconciliation,
/// drawdown/loss-limit/kill-switch state, and sequencing through
/// OrderManager. Same layering principle as everywhere else in this
/// codebase: decision engines and safety/orchestration engines stay
/// separate, composed by whoever wires the live host process together.
/// </summary>
public sealed record LiveTradingCycleInputs
{
    public required DateOnly AsOfDate { get; init; }
    public required DateTimeOffset Now { get; init; }
    public required decimal Nav { get; init; }

    public DrawdownState? PreviousDrawdownState { get; init; }
    public LossLimitState? PreviousLossLimitState { get; init; }
    public required KillSwitchState CurrentKillSwitchState { get; init; }

    public required RiskOptions RiskOptions { get; init; }
    public required LiveTradingOptions LiveTradingOptions { get; init; }

    public required IReadOnlyList<Position> LocalOpenPositions { get; init; }
    public required IReadOnlyList<Position> BrokerOpenPositions { get; init; }
    public required decimal LocalCashBalance { get; init; }
    public required decimal BrokerCashBalance { get; init; }

    /// <summary>Oldest-first; only the trailing run matters (see RepeatedRejectionDetector).</summary>
    public required IReadOnlyList<bool> RecentOrderRejections { get; init; }

    /// <summary>Oldest-first; only the trailing run matters (see AbnormalSlippageDetector).</summary>
    public required IReadOnlyList<decimal> RecentSlippageBps { get; init; }

    public required IReadOnlyList<(Order Order, OrderValidationInputs Validation)> ExitIntents { get; init; }
    public required IReadOnlyList<(Order Order, OrderValidationInputs Validation)> EntryIntents { get; init; }
}

public sealed record LiveTradingCycleResult(
    DrawdownState DrawdownState,
    LossLimitState LossLimitState,
    KillSwitchState KillSwitchState,
    ReconciliationResult Reconciliation,
    DrawdownEvent? DrawdownEvent,
    IReadOnlyList<SubmitOrderOutcome> SubmittedOrders);

public sealed class LiveTradingCycleEngine
{
    private readonly OrderManager _orderManager;

    public LiveTradingCycleEngine(OrderManager orderManager) => _orderManager = orderManager;

    public async Task<LiveTradingCycleResult> RunCycleAsync(LiveTradingCycleInputs inputs, CancellationToken cancellationToken = default)
    {
        var reconciliation = ReconciliationEngine.Reconcile(
            inputs.LocalOpenPositions, inputs.BrokerOpenPositions,
            inputs.LocalCashBalance, inputs.BrokerCashBalance,
            inputs.LiveTradingOptions.CashReconciliationToleranceRupees);

        var (drawdownState, drawdownEvent) = DrawdownEngine.Evaluate(inputs.PreviousDrawdownState, inputs.AsOfDate, inputs.Nav, inputs.RiskOptions);
        var lossLimitState = PeriodicLossLimitEngine.Evaluate(inputs.PreviousLossLimitState, inputs.AsOfDate, inputs.Nav, inputs.RiskOptions);

        var killSwitch = inputs.CurrentKillSwitchState;
        killSwitch = MaybeActivate(killSwitch, !reconciliation.IsClean, TradingPlatform.Core.Enums.KillSwitchSource.ReconciliationError,
            $"{reconciliation.Mismatches.Count} reconciliation mismatch(es) detected.", inputs.Now);
        killSwitch = MaybeActivate(killSwitch, drawdownState.IsTradingShutdown, TradingPlatform.Core.Enums.KillSwitchSource.Drawdown,
            $"Drawdown circuit breaker active ({drawdownState.DrawdownPercent:F1}%).", inputs.Now);
        killSwitch = MaybeActivate(killSwitch, lossLimitState.DailyLimitBreached, TradingPlatform.Core.Enums.KillSwitchSource.DailyLossLimit,
            "Daily loss limit breached.", inputs.Now);
        killSwitch = MaybeActivate(killSwitch, RepeatedRejectionDetector.ShouldTriggerKillSwitch(inputs.RecentOrderRejections, inputs.LiveTradingOptions.RejectionConsecutiveThreshold),
            TradingPlatform.Core.Enums.KillSwitchSource.RepeatedOrderRejection, $"{inputs.LiveTradingOptions.RejectionConsecutiveThreshold}+ consecutive order rejections.", inputs.Now);
        killSwitch = MaybeActivate(killSwitch,
            AbnormalSlippageDetector.ShouldTriggerKillSwitch(inputs.RecentSlippageBps, inputs.LiveTradingOptions.AbnormalSlippageThresholdBps, inputs.LiveTradingOptions.AbnormalSlippageConsecutiveThreshold),
            TradingPlatform.Core.Enums.KillSwitchSource.AbnormalSlippage, $"{inputs.LiveTradingOptions.AbnormalSlippageConsecutiveThreshold}+ consecutive fills beyond {inputs.LiveTradingOptions.AbnormalSlippageThresholdBps} bps slippage.", inputs.Now);

        var submitted = new List<SubmitOrderOutcome>();

        // Exits first — a risk-reducing order should never sit behind a queue of new entries,
        // and OrderManager/KillSwitchEngine already correctly allow these through even when the
        // switch above just activated (spec §6.4's exit carve-out, Tranche 12).
        foreach (var (order, validation) in inputs.ExitIntents)
            submitted.Add(await _orderManager.SubmitAsync(order, validation, killSwitch, cancellationToken).ConfigureAwait(false));

        foreach (var (order, validation) in inputs.EntryIntents)
            submitted.Add(await _orderManager.SubmitAsync(order, validation, killSwitch, cancellationToken).ConfigureAwait(false));

        return new LiveTradingCycleResult(drawdownState, lossLimitState, killSwitch, reconciliation, drawdownEvent, submitted);
    }

    private static KillSwitchState MaybeActivate(KillSwitchState current, bool triggered, TradingPlatform.Core.Enums.KillSwitchSource source, string reason, DateTimeOffset now) =>
        triggered ? KillSwitchEngine.Activate(current, source, reason, now) : current;
}
