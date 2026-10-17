namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>Persisted per-symbol/per-timestamp indicator values (Indicators table, spec §7.2).</summary>
public sealed class IndicatorSnapshot
{
    public long Id { get; set; }
    public required string Symbol { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public Timeframe Timeframe { get; set; }

    public decimal? HmaFast { get; set; }
    public decimal? HmaSlow { get; set; }
    public decimal? Supertrend { get; set; }
    public decimal? Sma20 { get; set; }
    public decimal? Sma50 { get; set; }
    public decimal? Sma200 { get; set; }
    public decimal? Rsi { get; set; }
    public decimal? Macd { get; set; }
    public decimal? MacdSignal { get; set; }
    public decimal? Adx { get; set; }
    public decimal? Atr { get; set; }
    public decimal? AtrPercent { get; set; }
    public decimal? HistoricalVolatility { get; set; }
    public decimal? VolumeSma { get; set; }
    public decimal? RelativeVolume { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
