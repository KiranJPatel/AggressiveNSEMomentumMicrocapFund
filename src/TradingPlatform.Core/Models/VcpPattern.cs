namespace TradingPlatform.Core.Models;

/// <summary>VCP detector output (spec §3.5).</summary>
public sealed class VcpPattern
{
    public long Id { get; set; }
    public required string Symbol { get; set; }
    public DateOnly AsOfDate { get; set; }

    public decimal VcpScore { get; set; }
    public int NumberOfContractions { get; set; }
    public required IReadOnlyList<decimal> ContractionRatios { get; set; }
    public decimal PivotPrice { get; set; }
    public int BaseLengthDays { get; set; }
    public decimal AverageVolumeDuringBase { get; set; }
    public decimal VolumeDryUpScore { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
