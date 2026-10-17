namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>Market regime engine output for a given date (spec §3.1).</summary>
public sealed class MarketRegimeSnapshot
{
    public long Id { get; set; }
    public DateOnly AsOfDate { get; set; }

    public int RegimeScore { get; set; } // 0-100
    public RegimeClassification Classification { get; set; }
    public decimal DeployableCapitalPercent { get; set; }

    public decimal? NiftyTrendScore { get; set; }
    public decimal? BreadthScore { get; set; }
    public decimal? AdvanceDeclineRatio { get; set; }
    public decimal? PercentUniverseAbove50Dma { get; set; }
    public decimal? PercentUniverseAbove200Dma { get; set; }
    public decimal? SmallMidRelativeStrength { get; set; }
    public decimal? VolatilityScore { get; set; }
    public decimal? BreakoutSuccessRate { get; set; }

    /// <summary>Weekly-timeframe bias of the broad index (spec §7.3). Null on snapshots written before the weekly bias existed.</summary>
    public WeeklyBias? WeeklyBias { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
