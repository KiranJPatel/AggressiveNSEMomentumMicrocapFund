namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>An open (or recently closed) position, including trailing-stop ladder state (spec §4.3).</summary>
public sealed class Position
{
    public long Id { get; set; }
    public required string Symbol { get; set; }

    public decimal Quantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal InitialStop { get; set; }
    public decimal CurrentStop { get; set; }
    public decimal InitialRiskAmount { get; set; }
    public decimal RiskPerShare { get; set; }

    public PyramidStage HighestStageFilled { get; set; }
    public bool IsOpen { get; set; }

    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public ExitReason? CloseReason { get; set; }
}
