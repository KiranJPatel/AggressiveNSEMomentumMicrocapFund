using TradingPlatform.Core.Enums;

namespace TradingPlatform.Core.Models;

/// <summary>End-of-day portfolio state snapshot (spec §7.2), also drives drawdown tracking.</summary>
public sealed class PortfolioSnapshot
{
    public long Id { get; set; }
    public DateOnly AsOfDate { get; set; }

    public decimal Nav { get; set; }
    public decimal CashBalance { get; set; }
    public decimal PeakNav { get; set; }
    public decimal DrawdownPercent { get; set; }
    public decimal PortfolioHeatPercent { get; set; }
    public int OpenPositionCount { get; set; }

    /// <summary>Added in Tranche 16 so RiskReport/BacktestReport can compute spec §8.1's "Regime — Bull: XX% Normal: XX% Bear: XX%" breakdown without re-deriving it after the fact.</summary>
    public RegimeClassification? Regime { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
