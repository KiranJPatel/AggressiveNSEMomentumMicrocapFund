namespace TradingPlatform.Core.Configuration;

/// <summary>Binds to config section "Portfolio" (spec §2.1). No magic numbers in code.</summary>
public sealed class PortfolioOptions
{
    public const string SectionName = "Portfolio";

    public decimal InitialCapital { get; set; } = 1_000_000m;
    public int MaxPositions { get; set; } = 10;
    public decimal MinimumCashPercent { get; set; } = 10m;
    public decimal MaxSinglePositionPercent { get; set; } = 15m;
    public decimal ExceptionalPositionPercent { get; set; } = 20m;
    public decimal MaxSectorExposurePercent { get; set; } = 25m;
    public decimal MaxMicrocapExposurePercent { get; set; } = 35m;
}
