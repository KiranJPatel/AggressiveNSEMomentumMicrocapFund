using TradingPlatform.Backtesting.WalkForward;
using TradingPlatform.Core.Models;

namespace TradingPlatform.Backtesting.Selection;

/// <summary>
/// Spec §5.5: "Selection uses a composite score (return, drawdown,
/// Sharpe, stability, trade count, out-of-sample performance) — never
/// CAGR alone." Spec names the components but not weights or a formula —
/// resolved here, documented rather than left an implicit guess:
///
///   - CAGR and Sharpe both count (spec explicitly rejects CAGR alone),
///     clamped so one extreme outlier value can't dominate the whole
///     score by itself.
///   - MaxDrawdownPercent is subtracted directly (every point of
///     drawdown costs one point of score) — deliberately steep, since
///     spec §5.7/§8.2 already treats &gt;40% drawdown as an automatic
///     rejection regardless of returns; the score should trend the same
///     direction well before that hard cutoff, not treat drawdown as a
///     minor factor that a good CAGR can paper over.
///   - TradeCount below a credibility floor is penalized (spec §5.7's
///     "too few trades" flag) — a strategy with 3 trades and a
///     eye-popping CAGR is not meaningfully better than one with 3 trades
///     and a mediocre one; both are statistically unreliable.
///   - Stability and out-of-sample performance are walk-forward-level
///     concepts (they need MULTIPLE windows to mean anything), so they
///     live in CalculateWalkForwardStability rather than
///     CalculateSingleRun — variance of the Test-window score across
///     windows is the stability component; how much worse Test performs
///     than Train is the out-of-sample-degradation component (directly
///     targeting spec §5.7's "attractive performance disappears
///     out-of-sample").
/// </summary>
public static class CompositeScoreCalculator
{
    public static decimal CalculateSingleRun(BacktestRun run, int minimumTradeCountForFullCredit = 30)
    {
        var cagr = run.Cagr ?? 0m;
        var sharpe = run.Sharpe ?? 0m;
        var maxDrawdown = run.MaxDrawdownPercent ?? 100m; // unknown drawdown is never treated optimistically
        var tradeCount = run.TradeCount ?? 0;

        var returnScore = Math.Clamp(cagr, -50m, 100m);
        var sharpeScore = Math.Clamp(sharpe, -3m, 3m) * 10m;
        var drawdownPenalty = maxDrawdown;
        var tradeCountPenalty = tradeCount >= minimumTradeCountForFullCredit ? 0m : (minimumTradeCountForFullCredit - tradeCount) * 1m;

        return returnScore + sharpeScore - drawdownPenalty - tradeCountPenalty;
    }

    public static decimal CalculateWalkForwardStability(IReadOnlyList<WalkForwardWindowResult> windows, int minimumTradeCountForFullCredit = 30)
    {
        if (windows.Count == 0) return 0m;

        var testScores = windows.Select(w => CalculateSingleRun(w.TestResult.Run, minimumTradeCountForFullCredit)).ToList();
        var averageTestScore = testScores.Average();

        var stabilityPenalty = testScores.Count >= 2
            ? (decimal)Math.Sqrt((double)(testScores.Sum(s => (s - averageTestScore) * (s - averageTestScore)) / (testScores.Count - 1)))
            : 0m;

        var trainScores = windows.Select(w => CalculateSingleRun(w.TrainResult.Run, minimumTradeCountForFullCredit)).ToList();
        var averageTrainScore = trainScores.Average();
        var outOfSampleDegradation = Math.Max(0m, averageTrainScore - averageTestScore);

        return averageTestScore - stabilityPenalty - outOfSampleDegradation;
    }
}
