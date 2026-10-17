using TradingPlatform.Execution.Reconciliation;
using TradingPlatform.Tests.Framework;

namespace TradingPlatform.Tests.ExecutionTests;

public class ReconciliationEngineTests
{
    private static TradingPlatform.Core.Models.Position Pos(string symbol, decimal qty, decimal avgEntry = 100m) => new()
    {
        Symbol = symbol, Quantity = qty, AverageEntryPrice = avgEntry, InitialStop = 90m, CurrentStop = 90m,
        InitialRiskAmount = 1000m, RiskPerShare = 10m, IsOpen = true, OpenedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void IdenticalPositionsAndCash_IsClean()
    {
        var local = new[] { Pos("ABC", 100m) };
        var broker = new[] { Pos("ABC", 100m) };

        var result = ReconciliationEngine.Reconcile(local, broker, 500_000m, 500_000m);

        Assert.True(result.IsClean);
        Assert.Equal(0, result.Mismatches.Count);
    }

    [Fact]
    public void PositionOnlyLocal_IsMismatch()
    {
        var local = new[] { Pos("ABC", 100m) };
        var broker = Array.Empty<TradingPlatform.Core.Models.Position>();

        var result = ReconciliationEngine.Reconcile(local, broker, 500_000m, 500_000m);

        Assert.False(result.IsClean);
        Assert.True(result.Mismatches.Any(m => m.Symbol == "ABC" && m.Field == "Existence"));
    }

    [Fact]
    public void PositionOnlyAtBroker_IsMismatch()
    {
        var local = Array.Empty<TradingPlatform.Core.Models.Position>();
        var broker = new[] { Pos("ABC", 100m) };

        var result = ReconciliationEngine.Reconcile(local, broker, 500_000m, 500_000m);

        Assert.False(result.IsClean);
        Assert.True(result.Mismatches.Any(m => m.Symbol == "ABC" && m.Field == "Existence"));
    }

    [Fact]
    public void QuantityMismatch_IsDetected_EvenByOneShare()
    {
        var local = new[] { Pos("ABC", 100m) };
        var broker = new[] { Pos("ABC", 99m) };

        var result = ReconciliationEngine.Reconcile(local, broker, 500_000m, 500_000m);

        Assert.False(result.IsClean);
        Assert.True(result.Mismatches.Any(m => m.Symbol == "ABC" && m.Field == "Quantity"));
    }

    [Fact]
    public void CashMismatch_BeyondTolerance_IsDetected()
    {
        var result = ReconciliationEngine.Reconcile(Array.Empty<TradingPlatform.Core.Models.Position>(), Array.Empty<TradingPlatform.Core.Models.Position>(), 500_000m, 495_000m, cashToleranceRupees: 1m);

        Assert.False(result.IsClean);
        Assert.True(result.Mismatches.Any(m => m.Field == "CashBalance"));
    }

    [Fact]
    public void CashMismatch_WithinTolerance_IsClean()
    {
        var result = ReconciliationEngine.Reconcile(Array.Empty<TradingPlatform.Core.Models.Position>(), Array.Empty<TradingPlatform.Core.Models.Position>(), 500_000.00m, 499_999.50m, cashToleranceRupees: 1m);

        Assert.True(result.IsClean);
    }

    [Fact]
    public void MultipleSymbols_OnlyMismatchedOnesReported()
    {
        var local = new[] { Pos("ABC", 100m), Pos("XYZ", 50m) };
        var broker = new[] { Pos("ABC", 100m), Pos("XYZ", 40m) };

        var result = ReconciliationEngine.Reconcile(local, broker, 500_000m, 500_000m);

        Assert.False(result.IsClean);
        Assert.Equal(1, result.Mismatches.Count);
        Assert.Equal("XYZ", result.Mismatches[0].Symbol);
    }
}
