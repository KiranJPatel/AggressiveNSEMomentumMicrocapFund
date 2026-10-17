namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>A generated entry/exit signal, versioned for reproducibility (spec §7.6).</summary>
public sealed class Signal
{
    public long Id { get; set; }
    public required string Symbol { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }

    public PyramidStage Stage { get; set; }
    public decimal PivotPrice { get; set; }
    public decimal SuggestedEntry { get; set; }
    public decimal SuggestedStop { get; set; }
    public decimal RiskPerShare { get; set; }
    public decimal SuggestedPositionQty { get; set; }

    public SignalToOrderLag SignalToOrderLag { get; set; }

    public required string StrategyVersion { get; set; }
    public required string ConfigurationVersion { get; set; }

    /// <summary>Which portfolio/risk constraint(s) actually bound when this signal was sized (spec §4.1).</summary>
    public string? BindingConstraints { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
