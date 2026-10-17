namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>
/// OHLCV cache record (spec §5.2). Unique on (Symbol, Timestamp, Timeframe).
/// Raw OHLC feeds stop-loss/execution simulation; AdjustedClose feeds return
/// calculation only — never the reverse (spec §5.3).
/// </summary>
public sealed class HistoricalBar
{
    public long Id { get; set; }
    public required string Symbol { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal AdjustedClose { get; set; }
    public long Volume { get; set; }
    public required string Source { get; set; }
    public Timeframe Timeframe { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
