namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>One pyramiding leg (stage fill) within a <see cref="Trade"/> (spec §3.7, §7.2).</summary>
public sealed class TradeLeg
{
    public long Id { get; set; }
    public long TradeId { get; set; }
    public PyramidStage Stage { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public DateTimeOffset FilledAt { get; set; }
}
