namespace TradingPlatform.Core.Models;

/// <summary>A drawdown circuit-breaker tier transition (spec §4.5).</summary>
public sealed class DrawdownEvent
{
    public long Id { get; set; }
    public decimal DrawdownPercent { get; set; }
    public required string TierName { get; set; }
    public decimal RiskReductionPercent { get; set; }
    public bool TradingShutdown { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
