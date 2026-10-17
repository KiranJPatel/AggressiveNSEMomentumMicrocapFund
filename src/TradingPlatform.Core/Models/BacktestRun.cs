namespace TradingPlatform.Core.Models;

using TradingPlatform.Core.Enums;

/// <summary>
/// A reproducible backtest run record (spec §7.6): every result must be
/// reproducible from this snapshot alone.
/// </summary>
public sealed class BacktestRun
{
    public long Id { get; set; }
    public required string StrategyVersion { get; set; }
    public required string ConfigurationHash { get; set; }
    public required string DataVersion { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public BacktestRunStatus Status { get; set; }

    /// <summary>Required field on the report object itself, not buried documentation (spec §5.6).</summary>
    public bool SurvivorshipBiased { get; set; }

    public decimal? FinalCapital { get; set; }
    public decimal? Cagr { get; set; }
    public decimal? MaxDrawdownPercent { get; set; }
    public decimal? Sharpe { get; set; }
    public decimal? Sortino { get; set; }
    public decimal? WinRatePercent { get; set; }
    public decimal? ProfitFactor { get; set; }
    public decimal? ExpectancyR { get; set; }
    public int? TradeCount { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
