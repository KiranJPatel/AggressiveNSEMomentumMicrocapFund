using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Application.Cli;

public sealed record OrderSyncResult(int Checked, int Filled, int Terminal, decimal CashDelta, int MissingAtBroker);

/// <summary>
/// Live order-status sync (spec §6.3: "never assume a fill because submission succeeded"). Kite returns
/// "Submitted" at placement, so the fill is only learned by polling the order book. Runs at the START of each live
/// cycle: every local non-terminal order that has a BrokerOrderId is looked up in the broker's order book and its
/// state advanced; a COMPLETE order is applied through the same <see cref="FillApplier"/> path paper uses, using the
/// <see cref="OrderIntent"/> persisted at submission. Intent-less orders (submitted before this existed) are advanced
/// but cannot create/close positions — reported loudly so reconciliation, not silence, surfaces them.
/// Partial fills: Kite shows them as OPEN with filled_quantity>0. Each sync applies only the NEW quantity (broker totals
/// are cumulative; already-applied fills are read back from the Fills table), the order becomes PartiallyFilled, and a
/// later slice of an entry grows the existing position. A cancel/reject AFTER a partial fill keeps the filled shares. Charges are not in Kite's order payload, so live fill commission is recorded as 0.
/// </summary>
public static class LiveOrderSync
{
    public static async Task<OrderSyncResult> RunAsync(
        Func<CancellationToken, Task<IReadOnlyList<Order>>> fetchBrokerOrders,
        IOrderRepository orderRepo, IPositionRepository positionRepo, ITradeRepository tradeRepo,
        ISignalRepository signalRepo, IRiskEventRepository riskEventRepo, IEngineStateRepository engineState,
        INotificationService? notifier, ILogger logger, CancellationToken cancellationToken)
    {
        var local = (await orderRepo.GetOpenOrdersAsync(cancellationToken)).Where(o => !string.IsNullOrEmpty(o.BrokerOrderId)).ToList();
        if (local.Count == 0) return new OrderSyncResult(0, 0, 0, 0m, 0);

        var broker = (await fetchBrokerOrders(cancellationToken)).Where(b => b.BrokerOrderId is not null)
            .GroupBy(b => b.BrokerOrderId!).ToDictionary(g => g.Key, g => g.Last());

        int filled = 0, terminal = 0, missing = 0; var cash = 0m;
        foreach (var order in local)
        {
            if (!broker.TryGetValue(order.BrokerOrderId!, out var b))
            {
                missing++;
                logger.LogWarning("order-sync: local order {Id} ({Symbol}) broker id {BrokerId} not in today's broker order book.", order.Id, order.Symbol, order.BrokerOrderId);
                continue;
            }
            if (b.State == OrderState.Unknown) continue;

            // Fills already applied locally for this order (idempotency anchor: sync can be re-run any number of times).
            var priorFills = await orderRepo.GetFillsAsync(order.Id, cancellationToken);
            var appliedQty = priorFills.Sum(f => f.Quantity);
            var appliedNotional = priorFills.Sum(f => f.Quantity * f.Price);

            // Broker totals are CUMULATIVE. COMPLETE with no reported quantity means the whole order.
            var brokerFilled = b.State == OrderState.Filled && b.FilledQuantity <= 0 ? order.Quantity : b.FilledQuantity;
            var delta = brokerFilled - appliedQty;

            // Kite shows a partial fill as OPEN with filled_quantity > 0 — surface that as PartiallyFilled locally.
            var target = b.State is OrderState.Open or OrderState.Acknowledged or OrderState.Submitted && brokerFilled > 0 ? OrderState.PartiallyFilled : b.State;
            if (delta <= 0 && target == order.State) continue;

            if (delta > 0)
            {
                filled++;
                // Price of just the NEW shares, from the broker's cumulative average.
                decimal? incPrice = null;
                if (b.AverageFillPrice is { } avg)
                {
                    var p = (avg * brokerFilled - appliedNotional) / delta;
                    incPrice = p > 0 ? p : avg;
                }
                order.FilledQuantity = delta;
                order.AverageFillPrice = incPrice;

                var intentJson = await engineState.GetAsync(OrderIntent.StateKey(order.CorrelationId), cancellationToken);
                var intent = intentJson is null ? null : JsonSerializer.Deserialize<OrderIntent>(intentJson);
                if (intent is null)
                {
                    logger.LogError("order-sync: order {Id} ({Symbol}) filled but has no stored intent — position NOT updated; reconcile manually.", order.Id, order.Symbol);
                    // The fill itself is still recorded so the fill history is honest and the next sync sees delta = 0.
                    await orderRepo.RecordFillAsync(new Fill { OrderId = order.Id, Symbol = order.Symbol, Quantity = delta, Price = incPrice ?? 0m, SlippageAmount = 0m, Commission = 0m, FilledAt = DateTimeOffset.UtcNow }, cancellationToken);
                    if (notifier is not null) await notifier.NotifyAsync(NotificationCategories.ReconciliationFailure, $"{order.Side} {order.Symbol} filled at the broker but local intent is missing — reconcile manually.", cancellationToken);
                }
                else
                {
                    // A later slice of an ENTRY must grow the position the first slice created, not open a second one.
                    if (intent.Kind == "Entry" && appliedQty > 0) intent = intent with { Kind = "Add", Stage = null };
                    if (intent.Kind != "Exit" && incPrice is { } px) order.FillSlippageAmount = Math.Abs(px - intent.SuggestedEntry) * delta;
                    var position = intent.Kind == "Exit" ? await positionRepo.GetOpenBySymbolAsync(order.Symbol, cancellationToken) : null;
                    cash += await FillApplier.ApplyAsync(order, order.Id, intent, position, orderRepo, positionRepo, tradeRepo, signalRepo, riskEventRepo, engineState, cancellationToken);
                    if (notifier is not null) await notifier.NotifyAsync(NotificationCategories.Fill,
                        $"{order.Side} {delta:N0} {order.Symbol} filled @ {incPrice:N2} (synced{(brokerFilled < order.Quantity ? $", {brokerFilled:N0}/{order.Quantity:N0} so far" : "")}).", cancellationToken);
                }
            }

            // Transition AFTER applying: if applying threw, the order stays open and the next sync retries; the fill rows anchor idempotency.
            if (target != order.State)
                await orderRepo.RecordStateTransitionAsync(order.Id, order.State, target, order.BrokerOrderId, b.RejectionReason, "order-sync", DateTimeOffset.UtcNow, cancellationToken);

            if (target is OrderState.Rejected or OrderState.Cancelled or OrderState.Failed)
            {
                terminal++;
                var partial = brokerFilled > 0 && brokerFilled < order.Quantity ? $" (only {brokerFilled:N0}/{order.Quantity:N0} filled)" : "";
                if (notifier is not null) await notifier.NotifyAsync(target == OrderState.Cancelled ? NotificationCategories.Order : NotificationCategories.Rejection,
                    $"{order.Side} {order.Quantity:N0} {order.Symbol} {target} at broker{partial}{(string.IsNullOrEmpty(b.RejectionReason) ? "" : ": " + b.RejectionReason)}.", cancellationToken);
            }
        }
        return new OrderSyncResult(local.Count, filled, terminal, cash, missing);
    }
}
