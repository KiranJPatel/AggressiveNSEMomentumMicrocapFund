using TradingPlatform.Core.Models;

namespace TradingPlatform.Execution.Reconciliation;

/// <summary>One detected mismatch between broker and local state.</summary>
public sealed record ReconciliationMismatch(string Symbol, string Field, string LocalValue, string BrokerValue);

/// <summary>
/// Outcome of one reconciliation pass (spec §6.4/§6.5). IsClean is false
/// the moment ANY mismatch exists — there is deliberately no "small
/// enough to ignore" tolerance band here: spec §6.4 is explicit that any
/// mismatch means halt, alert, and require manual intervention, never
/// auto-guess a fix. A quantity that differs by even one share is exactly
/// as much a "halt" event as a symbol present on one side and missing on
/// the other.
/// </summary>
public sealed record ReconciliationResult(bool IsClean, IReadOnlyList<ReconciliationMismatch> Mismatches);

/// <summary>
/// Compares broker-reported positions against local (SQLite) positions
/// (spec §6.4: "At startup and every trading cycle: fetch broker
/// positions/open orders/order history/funds, compare against SQLite. Any
/// mismatch → halt trading, alert, require manual intervention — never
/// auto-guess a fix.") and cash/funds. Pure comparison — the caller
/// supplies both sides (already fetched via IPositionService/
/// IAccountService) and is responsible for actually halting/alerting on a
/// dirty result; this engine only detects and reports.
/// </summary>
public static class ReconciliationEngine
{
    public static ReconciliationResult Reconcile(
        IReadOnlyList<Position> localOpenPositions,
        IReadOnlyList<Position> brokerOpenPositions,
        decimal localCashBalance,
        decimal brokerCashBalance,
        decimal cashToleranceRupees = 0m)
    {
        var mismatches = new List<ReconciliationMismatch>();

        var localBySymbol = localOpenPositions.ToDictionary(p => p.Symbol);
        var brokerBySymbol = brokerOpenPositions.ToDictionary(p => p.Symbol);

        foreach (var symbol in localBySymbol.Keys.Union(brokerBySymbol.Keys).OrderBy(s => s))
        {
            var hasLocal = localBySymbol.TryGetValue(symbol, out var local);
            var hasBroker = brokerBySymbol.TryGetValue(symbol, out var broker);

            if (hasLocal && !hasBroker)
            {
                mismatches.Add(new ReconciliationMismatch(symbol, "Existence", $"open, qty={local!.Quantity}", "not found at broker"));
                continue;
            }
            if (!hasLocal && hasBroker)
            {
                mismatches.Add(new ReconciliationMismatch(symbol, "Existence", "not tracked locally", $"open, qty={broker!.Quantity}"));
                continue;
            }

            if (local!.Quantity != broker!.Quantity)
                mismatches.Add(new ReconciliationMismatch(symbol, "Quantity", local.Quantity.ToString(), broker.Quantity.ToString()));

            // Average entry price commonly carries small rounding differences from broker-side
            // charges/averaging conventions — still reported (never silently dropped), but not
            // rounded away here; the caller decides materiality if it ever wants a softer policy.
            if (local.AverageEntryPrice != broker.AverageEntryPrice)
                mismatches.Add(new ReconciliationMismatch(symbol, "AverageEntryPrice", local.AverageEntryPrice.ToString("F4"), broker.AverageEntryPrice.ToString("F4")));
        }

        if (Math.Abs(localCashBalance - brokerCashBalance) > cashToleranceRupees)
            mismatches.Add(new ReconciliationMismatch("(account)", "CashBalance", localCashBalance.ToString("F2"), brokerCashBalance.ToString("F2")));

        return new ReconciliationResult(mismatches.Count == 0, mismatches);
    }
}
