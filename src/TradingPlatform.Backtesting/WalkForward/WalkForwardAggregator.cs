using TradingPlatform.Backtesting.Engine;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Backtesting.WalkForward;

/// <summary>Out-of-sample performance of a walk-forward run, chained across its non-overlapping Test periods (spec §8.2: out-of-sample CAGR and drawdown).</summary>
public sealed record WalkForwardAggregate(
    int WindowsTotal, int WindowsChained, int WindowsSkippedAsOverlapping,
    decimal ChainedReturnPercent, decimal? OutOfSampleCagrPercent, decimal OutOfSampleMaxDrawdownPercent,
    decimal WorstWindowReturnPercent, decimal WorstWindowDrawdownPercent, int TotalTrades, decimal? WinRatePercent,
    int ProfitableWindows, int TestedCalendarDays);

public static class WalkForwardAggregator
{
    /// <summary>
    /// Every Test window starts from the configured initial capital, so windows are not additive. They are chained instead: window i's
    /// daily NAV is rescaled by (chained value so far / initial capital), giving one continuous out-of-sample equity curve whose peak-
    /// to-trough drawdown can span windows. Test periods that overlap an earlier chained one (step shorter than the test length) are
    /// skipped — counting the same calendar days twice would inflate both return and the trade count — and reported, not hidden.
    /// CAGR is annualised over the calendar days actually tested (gaps between windows are not counted).
    /// </summary>
    public static WalkForwardAggregate Aggregate(IReadOnlyList<WalkForwardWindowResult> windows, decimal initialCapital)
    {
        if (initialCapital <= 0m) throw new ArgumentOutOfRangeException(nameof(initialCapital), "must be positive");

        var chained = ChainedWindows(windows);

        var value = 1m;              // chained equity as a multiple of starting equity
        var peak = 1m;
        var maxDrawdown = 0m;
        var worstReturn = decimal.MaxValue;
        var worstWindowDd = 0m;
        var trades = 0;
        var winners = 0;
        var profitable = 0;
        var days = 0;

        foreach (var w in chained)
        {
            var run = w.TestResult.Run;
            var startValue = value;
            var snapshots = w.TestResult.DailySnapshots.OrderBy(s => s.AsOfDate).ToList();
            foreach (var s in snapshots)
            {
                var current = startValue * (s.Nav / initialCapital);
                if (current > peak) peak = current;
                var dd = peak > 0m ? (peak - current) / peak * 100m : 0m;
                if (dd > maxDrawdown) maxDrawdown = dd;
            }

            var windowReturn = run.FinalCapital is { } fc ? fc / initialCapital - 1m : (snapshots.Count > 0 ? snapshots[^1].Nav / initialCapital - 1m : 0m);
            value = startValue * (1m + windowReturn);
            if (value > peak) peak = value;
            worstReturn = Math.Min(worstReturn, windowReturn * 100m);
            if (windowReturn > 0m) profitable++;
            worstWindowDd = Math.Max(worstWindowDd, run.MaxDrawdownPercent ?? 0m);
            trades += w.TestResult.Trades.Count;
            winners += w.TestResult.Trades.Count(t => t.RealizedPnl > 0m);
            days += w.Window.TestEnd.DayNumber - w.Window.TestStart.DayNumber + 1;
        }

        decimal? cagr = null;
        if (chained.Count > 0 && days >= 30 && value > 0m)
            cagr = (decimal)(Math.Pow((double)value, 365.0 / days) - 1.0) * 100m;

        return new WalkForwardAggregate(
            windows.Count, chained.Count, windows.Count - chained.Count,
            (value - 1m) * 100m, cagr, maxDrawdown,
            chained.Count > 0 ? worstReturn : 0m, worstWindowDd, trades, trades > 0 ? 100m * winners / trades : null,
            profitable, days);
    }

    /// <summary>
    /// The chained out-of-sample result as one <see cref="BacktestRunResult"/> so the same anti-overfitting checks that judge a single
    /// backtest (drawdown ceiling, trade-count floor, single-stock/year/regime dependence) can judge the whole walk-forward run.
    /// </summary>
    public static BacktestRunResult ToPooledResult(IReadOnlyList<WalkForwardWindowResult> windows, WalkForwardAggregate aggregate, decimal initialCapital)
    {
        var chained = ChainedWindows(windows);
        var trades = chained.SelectMany(w => w.TestResult.Trades).ToList();
        var run = new BacktestRun
        {
            StrategyVersion = chained.Count > 0 ? chained[0].TestResult.Run.StrategyVersion : "walkforward",
            ConfigurationHash = chained.Count > 0 ? chained[0].TestResult.Run.ConfigurationHash : "",
            DataVersion = chained.Count > 0 ? chained[0].TestResult.Run.DataVersion : "",
            StartDate = chained.Count > 0 ? chained[0].Window.TestStart : default,
            EndDate = chained.Count > 0 ? chained[^1].Window.TestEnd : default,
            Status = Core.Enums.BacktestRunStatus.Completed,
            SurvivorshipBiased = chained.Any(w => w.TestResult.Run.SurvivorshipBiased),
            FinalCapital = initialCapital * (1m + aggregate.ChainedReturnPercent / 100m),
            Cagr = aggregate.OutOfSampleCagrPercent,
            MaxDrawdownPercent = aggregate.OutOfSampleMaxDrawdownPercent,
            TradeCount = aggregate.TotalTrades,
            WinRatePercent = aggregate.WinRatePercent,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        return new BacktestRunResult
        {
            Run = run, Trades = trades, TradeLegs = Array.Empty<TradeLeg>(),
            DailySnapshots = Array.Empty<PortfolioSnapshot>(), DrawdownEvents = Array.Empty<DrawdownEvent>(),
        };
    }

    private static List<WalkForwardWindowResult> ChainedWindows(IReadOnlyList<WalkForwardWindowResult> windows)
    {
        var chained = new List<WalkForwardWindowResult>();
        DateOnly? lastEnd = null;
        foreach (var w in windows.OrderBy(w => w.Window.TestStart))
        {
            if (lastEnd is { } end && w.Window.TestStart <= end) continue;
            chained.Add(w);
            lastEnd = w.Window.TestEnd;
        }
        return chained;
    }
}
