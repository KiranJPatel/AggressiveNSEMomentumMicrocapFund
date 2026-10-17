using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Enums;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Tests.BacktestingTests;

/// <summary>Builds minimal, synthetic BacktestRunResult/Trade objects for testing WalkForward/Selection-layer engines, which operate on already-computed results rather than raw bars — no need for BacktestEngineFixture's breakout-condition machinery here.</summary>
public static class SelectionFixture
{
    public static Trade MakeTrade(string symbol, decimal realizedPnl, decimal initialRiskAmount = 1000m, int regimeScoreAtEntry = 60, DateTimeOffset? entryAt = null) => new()
    {
        Id = 0,
        Symbol = symbol,
        EntryPrice = 100m,
        InitialStop = 90m,
        InitialRiskAmount = initialRiskAmount,
        ExitPrice = 100m + realizedPnl / 10m,
        ExitReason = ExitReason.TrailingStopHit,
        RealizedPnl = realizedPnl,
        RMultiple = initialRiskAmount > 0 ? realizedPnl / initialRiskAmount : 0m,
        MaximumFavorableExcursion = 0m,
        MaximumAdverseExcursion = 0m,
        RegimeScoreAtEntry = regimeScoreAtEntry,
        StockScoreAtEntry = 60m,
        RiskAtEntryPercent = 1m,
        StrategyVersion = "v1",
        ConfigurationVersion = "c1",
        EntryAt = entryAt ?? new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5)),
        ExitAt = (entryAt ?? new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.FromHours(5.5))).AddDays(10),
    };

    public static BacktestRunResult MakeResult(
        DateOnly startDate, DateOnly endDate, IReadOnlyList<Trade> trades,
        decimal? cagr = null, decimal? sharpe = null, decimal? maxDrawdownPercent = 10m, bool survivorshipBiased = false)
    {
        var run = new BacktestRun
        {
            StrategyVersion = "v1",
            ConfigurationHash = "c1",
            DataVersion = "d1",
            StartDate = startDate,
            EndDate = endDate,
            Status = BacktestRunStatus.Completed,
            SurvivorshipBiased = survivorshipBiased,
            FinalCapital = 1_000_000m + trades.Sum(t => t.RealizedPnl),
            Cagr = cagr,
            MaxDrawdownPercent = maxDrawdownPercent,
            Sharpe = sharpe,
            Sortino = sharpe,
            WinRatePercent = trades.Count > 0 ? trades.Count(t => t.RealizedPnl > 0) / (decimal)trades.Count * 100m : 0m,
            ProfitFactor = 1m,
            ExpectancyR = trades.Count > 0 ? trades.Average(t => t.RMultiple) : 0m,
            TradeCount = trades.Count,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        return new BacktestRunResult
        {
            Run = run,
            Trades = trades,
            TradeLegs = Array.Empty<TradeLeg>(),
            DailySnapshots = Array.Empty<PortfolioSnapshot>(),
            DrawdownEvents = Array.Empty<DrawdownEvent>(),
        };
    }
}
