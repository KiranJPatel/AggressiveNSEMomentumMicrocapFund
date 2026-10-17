namespace TradingPlatform.Core.Models;

/// <summary>
/// Output of the position-sizing calculation (spec §4.1). Every candidate
/// quantity is retained so the binding (minimum) constraint can be proven
/// in unit tests, not just asserted.
/// </summary>
public sealed class PositionSizingResult
{
    public required string Symbol { get; set; }

    public decimal RiskBasedQty { get; set; }
    public decimal MaxSinglePositionQty { get; set; }
    public decimal LiquidityConstrainedQty { get; set; }
    public decimal SectorConstrainedQty { get; set; }
    public decimal MicrocapConstrainedQty { get; set; }
    public decimal CashConstrainedQty { get; set; }
    public decimal RegimeAdjustedQty { get; set; }
    public decimal PortfolioHeatConstrainedQty { get; set; }

    /// <summary>
    /// Caps this trade so that, combined with existing positions highly
    /// correlated with it (60-day rolling correlation, spec §4.2), the
    /// cluster's aggregate exposure doesn't exceed a configured limit —
    /// "ten highly-correlated names must not count as ten independent
    /// bets." Not one of the spec's literally-named 8 constraints, but a
    /// direct, justified operationalization of its correlation requirement
    /// using the same aggregate-exposure-cap mechanism as SectorConstrainedQty.
    /// </summary>
    public decimal CorrelationConstrainedQty { get; set; }

    public decimal FinalQty { get; set; }

    /// <summary>Name(s) of the constraint(s) that equal FinalQty (i.e. actually bound).</summary>
    public required IReadOnlyList<string> BindingConstraints { get; set; }

    public bool Rejected { get; set; }
    public string? RejectionReason { get; set; }
}
