using TradingPlatform.Core.Configuration;
using TradingPlatform.Core.Models;
using TradingPlatform.Execution.Orchestration;
using TradingPlatform.Risk.Drawdown;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class RestartRecoveryEngineTests
{
    private static readonly RiskOptions RiskOptions = new() { DrawdownTiers = RiskOptions.DefaultDrawdownTiers.ToList() };
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    private static Position Pos(string symbol, decimal qty) => new() { Symbol = symbol, Quantity = qty, IsOpen = true };

    [Fact]
    public void BrokerConnectionNotValidated_NeverSafeToResume()
    {
        var result = RestartRecoveryEngine.Evaluate(false, Array.Empty<Position>(), Array.Empty<Position>(), 100m, 100m, null);
        Assert.False(result.SafeToResume);
    }

    [Fact]
    public void CleanReconciliation_NoDrawdownIssue_IsSafeToResume()
    {
        var result = RestartRecoveryEngine.Evaluate(true, new[] { Pos("A", 10m) }, new[] { Pos("A", 10m) }, 100m, 100m, null);
        Assert.True(result.SafeToResume);
        Assert.True(result.HaltReason is null);
    }

    [Fact]
    public void ReconciliationMismatch_NeverSafeToResume()
    {
        var result = RestartRecoveryEngine.Evaluate(true, new[] { Pos("A", 10m) }, new[] { Pos("A", 15m) }, 100m, 100m, null);
        Assert.False(result.SafeToResume);
        Assert.False(result.Reconciliation.IsClean);
    }

    [Fact]
    public void DrawdownShutdown_NeverSafeToResume_EvenWithCleanReconciliation()
    {
        var (initial, _) = DrawdownEngine.Evaluate(null, Day0, 1_000_000m, RiskOptions);
        var (shutdown, _) = DrawdownEngine.Evaluate(initial, Day0.AddDays(1), 580_000m, RiskOptions); // 42% drawdown -> shutdown

        var result = RestartRecoveryEngine.Evaluate(true, Array.Empty<Position>(), Array.Empty<Position>(), 100m, 100m, shutdown);

        Assert.False(result.SafeToResume);
        Assert.True(result.HaltReason!.Contains("manual reset"));
    }

    [Fact]
    public void RecoveredDrawdown_ButLatchStillEngaged_StillNotSafe()
    {
        // Mirrors DrawdownEngine's own latch semantics (Tranche 10): recovery alone never clears it.
        var (initial, _) = DrawdownEngine.Evaluate(null, Day0, 1_000_000m, RiskOptions);
        var (shutdown, _) = DrawdownEngine.Evaluate(initial, Day0.AddDays(1), 580_000m, RiskOptions);
        var (recovered, _) = DrawdownEngine.Evaluate(shutdown, Day0.AddDays(2), 1_200_000m, RiskOptions); // full recovery, no manual reset acknowledged

        var result = RestartRecoveryEngine.Evaluate(true, Array.Empty<Position>(), Array.Empty<Position>(), 100m, 100m, recovered);

        Assert.False(result.SafeToResume);
    }

    [Fact]
    public void NullDrawdownState_TreatedAsNoIssue()
    {
        // First-ever run: no prior drawdown state exists yet.
        var result = RestartRecoveryEngine.Evaluate(true, Array.Empty<Position>(), Array.Empty<Position>(), 100m, 100m, null);
        Assert.True(result.SafeToResume);
    }
}
