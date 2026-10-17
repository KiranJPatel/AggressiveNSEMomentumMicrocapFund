namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>
/// Point-in-time fundamental data, with explicit per-field availability
/// (spec §3.2). Do not use a field whose Availability is not Available.
/// </summary>
public sealed class FundamentalSnapshot
{
    public required string Symbol { get; set; }
    public DateOnly AsOfDate { get; set; }

    public decimal? EarningsGrowthPercent { get; set; }
    public DataAvailability EarningsGrowthAvailability { get; set; }

    public decimal? RevenueGrowthPercent { get; set; }
    public DataAvailability RevenueGrowthAvailability { get; set; }
}
