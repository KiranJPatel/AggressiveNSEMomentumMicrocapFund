namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>Stock scoring engine output, 100-point composite (spec §3.2).</summary>
public sealed class StockScore
{
    public long Id { get; set; }
    public required string Symbol { get; set; }
    public DateOnly AsOfDate { get; set; }

    public decimal TotalScore { get; set; }
    public decimal TechnicalScore { get; set; }
    public decimal FundamentalScore { get; set; }
    public decimal RelativeStrengthScore { get; set; }   // weight 20
    public decimal VolumeScore { get; set; }              // weight 15 (Volume Accumulation)
    public decimal PriceMomentumScore { get; set; }       // weight 15
    public decimal EarningsAccelerationScore { get; set; }// weight 15
    public decimal VcpScore { get; set; }                 // weight 10
    public decimal RevenueAccelerationScore { get; set; } // weight 10
    public decimal TrendStructureScore { get; set; }      // weight 5
    public decimal InstitutionalActivityScore { get; set; }// weight 5
    public decimal SectorScore { get; set; }               // weight 5

    /// <summary>Reflects how many inputs were Unknown/Unavailable rather than genuinely scored (spec §3.2).</summary>
    public decimal DataQualityScore { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
