namespace TradingPlatform.Core.Interfaces;

using TradingPlatform.Core.Models;

/// <summary>
/// Provider-agnostic order execution seam (spec §1.2). Backtest/Paper/Zerodha
/// implementations differ only here — the shared StrategyStepEngine's
/// decision logic must be identical regardless of which is injected.
/// </summary>
public interface IExecutionProvider
{
    Task<Order> SubmitOrderAsync(Order order, CancellationToken cancellationToken = default);
    Task<Order> CancelOrderAsync(long orderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default);
}
