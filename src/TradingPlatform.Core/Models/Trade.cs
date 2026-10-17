namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>
/// Immutable completed-trade journal entry (spec §7.2). Never overwritten —
/// every field required to reconstruct why the trade was taken and how it
/// was managed is captured here.
/// </summary>
public sealed class Trade
{
    public long Id { get; set; }
    public required string Symbol { get; set; }

    public long? SignalId { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal InitialStop { get; set; }
    public decimal InitialRiskAmount { get; set; }
    public decimal ExitPrice { get; set; }
    public ExitReason ExitReason { get; set; }

    public decimal RealizedPnl { get; set; }
    public decimal RMultiple { get; set; }
    public decimal MaximumFavorableExcursion { get; set; }
    public decimal MaximumAdverseExcursion { get; set; }

    public int RegimeScoreAtEntry { get; set; }
    public decimal StockScoreAtEntry { get; set; }
    public decimal RiskAtEntryPercent { get; set; }

    public required string StrategyVersion { get; set; }
    public required string ConfigurationVersion { get; set; }

    public DateTimeOffset EntryAt { get; set; }
    public DateTimeOffset ExitAt { get; set; }
}
