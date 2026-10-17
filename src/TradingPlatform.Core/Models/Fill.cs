namespace TradingPlatform.Core.Models;

/// <summary>An execution fill (full or partial) against an order.</summary>
public sealed class Fill
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public required string Symbol { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal SlippageAmount { get; set; }
    public decimal Commission { get; set; }
    public DateTimeOffset FilledAt { get; set; }
}
