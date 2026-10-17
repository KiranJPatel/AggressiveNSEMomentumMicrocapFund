namespace TradingPlatform.Core.Models;

/// <summary>A single simulated trade produced within a <see cref="BacktestRun"/>.</summary>
public sealed class BacktestTrade
{
    public long Id { get; set; }
    public long BacktestRunId { get; set; }
    public long TradeId { get; set; }
}
