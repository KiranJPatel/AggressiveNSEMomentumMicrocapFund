namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>Broker-facing order execution service (spec §6.2-§6.3).</summary>
public interface IOrderExecutionService
{
    Task<Order> PlaceOrderAsync(Order order, CancellationToken cancellationToken = default);
    Task<Order> CancelOrderAsync(long orderId, CancellationToken cancellationToken = default);
}
