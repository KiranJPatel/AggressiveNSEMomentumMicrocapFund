namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>Immutable append-only log of every order state transition (spec §7.2).</summary>
public sealed class OrderEvent
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public OrderState FromState { get; set; }
    public OrderState ToState { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
