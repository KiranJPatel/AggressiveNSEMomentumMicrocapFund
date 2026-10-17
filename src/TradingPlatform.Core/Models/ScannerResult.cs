namespace TradingPlatform.Core.Models;

/// <summary>One row of scanner output (spec §7.1) — top-N ranked candidates for a scan cycle.</summary>
public sealed class ScannerResult
{
    public long Id { get; set; }
    public DateTimeOffset ScanTimestamp { get; set; }
    public int Rank { get; set; }
    public required string Symbol { get; set; }
    public decimal Price { get; set; }
    public decimal? MarketCap { get; set; }
    public string? Sector { get; set; }
    public decimal Score { get; set; }
    public decimal RelativeStrengthScore { get; set; }
    public decimal MomentumScore { get; set; }
    public decimal VolumeScore { get; set; }
    public decimal VcpScore { get; set; }
    public decimal? Adx { get; set; }
    public decimal? Rsi { get; set; }
    public decimal? Atr { get; set; }
    public decimal PivotPrice { get; set; }
    public required string BreakoutStatus { get; set; }
    public decimal DistanceFromPivotPercent { get; set; }
    public decimal SuggestedEntry { get; set; }
    public decimal SuggestedStop { get; set; }
    public decimal Risk { get; set; }
    public decimal SuggestedPositionSize { get; set; }
}
