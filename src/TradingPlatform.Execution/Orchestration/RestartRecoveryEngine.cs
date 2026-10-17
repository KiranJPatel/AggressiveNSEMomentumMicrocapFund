using TradingPlatform.Core.Models;
using TradingPlatform.Execution.Reconciliation;
using TradingPlatform.Risk.Drawdown;

namespace TradingPlatform.Execution.Orchestration;

public sealed record RestartRecoveryResult(bool SafeToResume, string? HaltReason, ReconciliationResult Reconciliation);

/// <summary>
/// Spec §6.5's restart sequence: "load DB state, connect to broker,
/// reconcile, reconstruct portfolio, validate risk — only then resume
/// order generation." The I/O steps (loading DB state, connecting to the
/// broker, fetching its current positions/cash) are the caller's job —
/// this engine is the pure DECISION at the end of that sequence, taking
/// whatever was already fetched and answering "is it actually safe to
/// resume."
///
/// Three gates, all must clear: the broker connection itself must be
/// confirmed live (spec §6.2's "handle... auth failure, expired token" —
/// resuming against a broken connection is worse than not resuming at
/// all); reconciliation between local and broker state must be clean
/// (reusing ReconciliationEngine, Tranche 12, unchanged — the same
/// zero-tolerance-on-positions rule that governs every OTHER cycle
/// applies with equal force to the FIRST one after a restart); and the
/// drawdown circuit breaker must not be in its latched shutdown state
/// (Tranche 10's DrawdownEngine — a restart must never be read as an
/// implicit manual reset of a 40%+ drawdown halt; that requires the
/// actual explicit acknowledgment DrawdownEngine's own contract demands).
/// </summary>
public static class RestartRecoveryEngine
{
    public static RestartRecoveryResult Evaluate(
        bool brokerConnectionValidated,
        IReadOnlyList<Position> localOpenPositions,
        IReadOnlyList<Position> brokerOpenPositions,
        decimal localCashBalance,
        decimal brokerCashBalance,
        DrawdownState? drawdownState,
        decimal cashToleranceRupees = 0m)
    {
        if (!brokerConnectionValidated)
            return new RestartRecoveryResult(false, "Broker connection could not be validated — cannot safely resume.", new ReconciliationResult(false, Array.Empty<ReconciliationMismatch>()));

        var reconciliation = ReconciliationEngine.Reconcile(localOpenPositions, brokerOpenPositions, localCashBalance, brokerCashBalance, cashToleranceRupees);
        if (!reconciliation.IsClean)
            return new RestartRecoveryResult(false, $"Reconciliation found {reconciliation.Mismatches.Count} mismatch(es) — manual intervention required before resuming.", reconciliation);

        if (drawdownState?.IsTradingShutdown == true)
            return new RestartRecoveryResult(false, "Drawdown circuit breaker is active and requires an explicit manual reset — a restart alone does not clear it.", reconciliation);

        return new RestartRecoveryResult(true, null, reconciliation);
    }
}
