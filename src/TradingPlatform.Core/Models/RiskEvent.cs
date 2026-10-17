namespace TradingPlatform.Core.Models;

/// <summary>A logged risk-rule evaluation (breach, near-breach, or binding constraint) (spec §7.2).</summary>
public sealed class RiskEvent
{
    public long Id { get; set; }
    public required string EventType { get; set; }
    public required string Description { get; set; }
    public string? Symbol { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string CorrelationId { get; set; }
}
