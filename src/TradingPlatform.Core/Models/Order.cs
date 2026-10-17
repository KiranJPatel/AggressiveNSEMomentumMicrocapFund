namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>Order record for the order state machine (spec §6.3).</summary>
public sealed class Order
{
    public long Id { get; set; }
    public long? SignalId { get; set; }
    public required string Symbol { get; set; }

    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public OrderState State { get; set; }

    public decimal Quantity { get; set; }
    public decimal? LimitPrice { get; set; }
    public decimal? StopPrice { get; set; }

    public string? BrokerOrderId { get; set; }
    public string? RejectionReason { get; set; }

    // Execution outcome as reported by the execution provider (paper simulation or broker). Not columns on the
    // Orders table: they are persisted as a Fill row (spec §7.2 Fills) so the immutable fill history stays the
    // single source of truth for price/quantity/costs. Null/0 until the provider has actually reported a fill.
    public decimal FilledQuantity { get; set; }
    public decimal? AverageFillPrice { get; set; }
    public decimal FillCommission { get; set; }
    public decimal FillSlippageAmount { get; set; }

    public required string CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
