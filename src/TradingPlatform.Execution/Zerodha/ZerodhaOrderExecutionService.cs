using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Interfaces;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Execution.Zerodha;

/// <summary>
/// Kite Connect order placement/cancellation (spec §6.2-§6.3). Implements
/// BOTH IOrderExecutionService (the Zerodha-specific broker contract) and
/// IExecutionProvider (the Backtest/Paper/Zerodha parity seam, spec
/// §1.2) — this is the seam's real, live-broker-facing implementation;
/// BacktestFillSimulator (Tranche 11) and PaperExecutionProvider (this
/// tranche) are its historical-replay and simulated-live counterparts.
///
/// UNVERIFIED (see KiteChecksumHelper's remarks): product is hard-coded
/// to CNC (Cash and Carry) since this platform trades NSE delivery equity
/// long-only (spec §2.2) and never intraday (MIS) or F&amp;O (NRML)
/// products — there's no branch here because there's genuinely only one
/// correct value for this system, not because other products aren't
/// documented. Stop orders (OrderType.Stop/StopLoss) map to Kite's SL-M
/// (stop-loss MARKET) rather than SL (stop-loss LIMIT) — once a
/// protective stop triggers, this system wants it to actually execute,
/// not risk resting unfilled at a limit price during a fast decline,
/// matching the same intraday-fill philosophy ExitEngine/
/// BacktestFillSimulator already established (Tranches 9 and 11).
/// GetOpenOrdersAsync fetches the full day's order book and filters
/// client-side to the states IExecutionProvider callers care about, since
/// Kite has no server-side "open orders only" filter parameter in the
/// documented v3 orders endpoint.
/// </summary>
public sealed class ZerodhaOrderExecutionService : IOrderExecutionService, IExecutionProvider
{
    private readonly KiteApiGateway _gateway;

    public ZerodhaOrderExecutionService(KiteApiGateway gateway) => _gateway = gateway;

    public async Task<Order> PlaceOrderAsync(Order order, CancellationToken cancellationToken = default)
    {
        var formParams = BuildOrderFormParams(order);

        try
        {
            var response = await _gateway.SendOrderRequestAsync(HttpMethod.Post, "/orders/regular", formParams, cancellationToken).ConfigureAwait(false);
            var brokerOrderId = KiteResponseParser.ParseOrderPlacement(response.StatusCode, response.Body);

            order.BrokerOrderId = brokerOrderId;
            order.State = OrderState.Submitted; // spec §6.3: "Never assume a fill because submission succeeded" — Submitted, not Filled
            order.UpdatedAt = DateTimeOffset.UtcNow;
            return order;
        }
        catch (KiteException ex)
        {
            order.State = OrderState.Failed;
            order.RejectionReason = ex.Message;
            order.UpdatedAt = DateTimeOffset.UtcNow;
            return order;
        }
    }

    public Task<Order> SubmitOrderAsync(Order order, CancellationToken cancellationToken = default) => PlaceOrderAsync(order, cancellationToken);

    public Task<Order> CancelOrderAsync(long orderId, CancellationToken cancellationToken = default)
    {
        // Kite identifies orders by its own broker_order_id (a string), not our local numeric Id —
        // callers must resolve that mapping before calling this; see OrderManager.
        throw new NotSupportedException(
            "ZerodhaOrderExecutionService.CancelOrderAsync(long) needs the broker order_id, not the local Order.Id — " +
            "use CancelBrokerOrderAsync(string brokerOrderId) instead. (Kept to satisfy IOrderExecutionService/IExecutionProvider's shared signature; " +
            "the Order Manager is expected to resolve local Id -> BrokerOrderId before calling through.)");
    }

    public async Task<Order> CancelBrokerOrderAsync(Order order, CancellationToken cancellationToken = default)
    {
        if (order.BrokerOrderId is null)
            throw new InvalidOperationException($"Order {order.Id} has no BrokerOrderId — it may never have reached the broker.");

        var response = await _gateway.SendOrderRequestAsync(HttpMethod.Delete, $"/orders/regular/{order.BrokerOrderId}", null, cancellationToken).ConfigureAwait(false);
        KiteResponseParser.ThrowIfError(response.StatusCode, response.Body);

        order.State = OrderState.Cancelled;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        return order;
    }

    /// <summary>Today's full order book from the broker (every status) — used by order-status sync to learn fills for orders we submitted earlier.</summary>
    public async Task<IReadOnlyList<Order>> GetAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        var response = await _gateway.SendGeneralRequestAsync(HttpMethod.Get, "/orders", null, cancellationToken).ConfigureAwait(false);
        return KiteResponseParser.ParseOrders(response.StatusCode, response.Body);
    }

    public async Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
    {
        var response = await _gateway.SendGeneralRequestAsync(HttpMethod.Get, "/orders", null, cancellationToken).ConfigureAwait(false);
        var allOrders = KiteResponseParser.ParseOrders(response.StatusCode, response.Body);
        return allOrders.Where(o => o.State is OrderState.Submitted or OrderState.Acknowledged or OrderState.Open or OrderState.PartiallyFilled).ToList();
    }

    private static Dictionary<string, string> BuildOrderFormParams(Order order)
    {
        var formParams = new Dictionary<string, string>
        {
            ["tradingsymbol"] = order.Symbol,
            ["exchange"] = "NSE",
            ["transaction_type"] = order.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["order_type"] = order.Type switch
            {
                OrderType.Market => "MARKET",
                OrderType.Limit => "LIMIT",
                OrderType.Stop or OrderType.StopLoss => "SL-M",
                _ => throw new ArgumentOutOfRangeException(nameof(order), order.Type, "Unsupported order type for Kite."),
            },
            ["quantity"] = ((long)order.Quantity).ToString(),
            ["product"] = "CNC",
            ["validity"] = "DAY",
        };

        if (order.Type == OrderType.Limit && order.LimitPrice is { } limitPrice)
            formParams["price"] = limitPrice.ToString("F2");
        if (order.Type is OrderType.Stop or OrderType.StopLoss && order.StopPrice is { } stopPrice)
            formParams["trigger_price"] = stopPrice.ToString("F2");

        return formParams;
    }
}
